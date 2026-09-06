using XTMF2.RuntimeModules;
using System.Collections.Concurrent;

namespace TMG.SurveyReweight;

[Module(
    Name = "SurveyReweight",
    Description = "Performs the survey reweighting process.",
    DocumentationLink = "https://tmg.utoronto.ca/doc/2.0/"
)]
public class SurveyReweight : BaseAction
{
    [SubModule(Required = true, Name = "ZoneSystem", Description = "The zone system to use for the reweighting process.", Index = 0)]
    public IFunction<ZoneSystem> ZoneSystem = null!;

    [SubModule(Required = true, Name = "SurveyRecords", Description = "The survey records to use for the reweighting process.", Index = 1)]
    public IFunction<ZoneSystem, SurveyRecords> SurveyRecords = null!;

    [SubModule(Required = true, Name = "Constraints", Description = "The constraints to use for the reweighting process.", Index = 2)]
    public IFunction<(ZoneSystem, SurveyRecords), Constraints> Constraints = null!;

    [Parameter(Name = "MaximumIterations", DefaultValue = "100", Description = "The maximum number of iterations for the reweighting process.", Index = 3)]
    public IFunction<int> MaximumIterations = null!;

    [Parameter(Name = "Damping", DefaultValue = "1.0", Description = "The damping applied to the combined constraint corrections. A value of one fully applies the corrections.", Index = 4)]
    public IFunction<float> Damping = null!;

    [SubModule(Required = true, Name = "UpdatedExpansionFactorsFile", Description = "The CSV file to write the updated expansion factors to.", Index = 5)]
    public IFunction<WriteStream> UpdatedExpansionFactorsFile = null!;

    [SubModule(Required = false, Name = "Log", Description = "The log for the reweighting process.", Index = 6)]
    public IFunction<Log>? Log;

    [SubModule(Required = false, Name = "ConstraintReportFile", Description = "The optional CSV file to write the final constraint report to.", Index = 7)]
    public IFunction<WriteStream>? ConstraintReportFile;

    public override void Invoke()
    {
        var log = Log?.Invoke();
        // Step 1 read in zone system and Super Zones
        log?.Invoke($"Reading in zone system and super zones...");
        var zoneSystem = ZoneSystem.Invoke();
        // Step 2 read in survey records (Household, Person, and Trip)
        log?.Invoke($"Reading in survey records...");
        var surveyRecords = SurveyRecords.Invoke(zoneSystem);
        // Step 3 read in constraints (Household, Person, and Trip)
        log?.Invoke($"Reading in constraints...");
        var constraints = Constraints.Invoke((zoneSystem, surveyRecords));
        var maxIterations = MaximumIterations.Invoke();
        var damping = Damping.Invoke();


        // Step 4 initialize the expansion factors and temporary compute space for the reweighting process
        log?.Invoke($"Initializing expansion factors and temporary compute space...");
        float[] currentExpansionFactors = new float[surveyRecords.NumberOfHouseholds];
        currentExpansionFactors.AsSpan().Fill(1.0f);

        // Scratch is used for aggregating the current total of expansion factors for the currently weighted expansions.
        // LogRatios is the logarithm of the correction each constraint wants applied to each household.
        // The corrections are combined as a damped geometric mean before being applied to the expansion factors.
        var householdCount = constraints.HouseholdConstraints.Length;
        var personCount = constraints.PersonConstraints.Length;
        var tripCount = constraints.TripConstraints.Length;

        var constraintData = new ConstraintData[householdCount + personCount + tripCount];
        int index = 0;
        foreach (var constraint in constraints.HouseholdConstraints)
        {
            constraintData[index++] = new ConstraintData(this, constraint, (int)surveyRecords.NumberOfHouseholds, null);
        }
        foreach (var constraint in constraints.PersonConstraints)
        {
            constraintData[index++] = new ConstraintData(this, constraint, (int)surveyRecords.NumberOfPersons, surveyRecords.PersonHouseholdIndex);
        }
        foreach (var constraint in constraints.TripConstraints)
        {
            constraintData[index++] = new ConstraintData(this, constraint, (int)surveyRecords.NumberOfTrips, surveyRecords.TripHouseholdIndex);
        }

        // Initialize the geography index for each constraint.
        for (int i = 0; i < householdCount; i++)
        {
            constraintData[i].InitializeGeographyIndexes(constraintData[i].Constraint.CategoryMap, j => surveyRecords.HouseholdTAZ[j]);
        }
        for (int i = 0; i < personCount; i++)
        {
            constraintData[householdCount + i].InitializeGeographyIndexes(constraintData[householdCount + i].Constraint.CategoryMap, j => surveyRecords.HouseholdTAZ[surveyRecords.PersonHouseholdIndex[j]]);
        }
        for (int i = 0; i < tripCount; i++)
        {
            constraintData[householdCount + personCount + i].InitializeGeographyIndexes(constraintData[householdCount + personCount + i].Constraint.CategoryMap, j => surveyRecords.HouseholdTAZ[surveyRecords.TripHouseholdIndex[j]]);
        }


        // Step 5 run the reweighting process
        int iteration = 0;
        for (; iteration < maxIterations; iteration++)
        {
            log?.Invoke($"Running iteration {iteration + 1} of {maxIterations}...");
            bool allConstraintsSatisfied = true;

            Parallel.Invoke(() =>
            {
                Parallel.For(0, constraints.HouseholdConstraints.Length, i =>
                {
                    var data = constraintData[i];

                    if (!constraints.HouseholdConstraints[i].UpdateConstraint(surveyRecords.HouseholdData, (nuint)surveyRecords.HouseholdAttributes.Length,
                     currentExpansionFactors, data.Scratch, data.ExpansionIndex, data.GeographyIndex, data.LogRatios))
                    {
                        allConstraintsSatisfied = false;
                    }
                });
            },
            () =>
            {
                Parallel.For(0, constraints.PersonConstraints.Length, i =>
                {
                    var data = constraintData[householdCount + i];

                    if (!constraints.PersonConstraints[i].UpdateConstraint(surveyRecords.PersonData, (nuint)surveyRecords.PersonAttributes.Length,
                         currentExpansionFactors, data.Scratch, data.ExpansionIndex, data.GeographyIndex, data.LogRatios))
                    {
                        allConstraintsSatisfied = false;
                    }
                });
            },
            () =>
            {
                Parallel.For(0, constraints.TripConstraints.Length, i =>
                {
                    var data = constraintData[householdCount + personCount + i];

                    if (!constraints.TripConstraints[i].UpdateConstraint(surveyRecords.TripData, (nuint)surveyRecords.TripAttributes.Length, currentExpansionFactors,
                         data.Scratch, data.ExpansionIndex, data.GeographyIndex, data.LogRatios))
                    {
                        allConstraintsSatisfied = false;
                    }
                });
            });

            // Check if all of the constraints are satisfied
            if (allConstraintsSatisfied)
            {
                break;
            }

            // If we have not already found a sufficient solution, combine the corrections from every constraint.
            // The correction for each household is the damped geometric mean of the per-constraint ratios:
            //   W_hh *= exp(damping * sum(ln(ratio_i)) / count)
            // where ratio_i is the correction the i-th constraint wants applied to that household (zero means the constraint does not affect it).
            var householdCountForUpdate = currentExpansionFactors.Length;
            var householdChunkSize = Math.Max(1024, householdCountForUpdate / (Environment.ProcessorCount * 4));
            Parallel.ForEach(Partitioner.Create(0, householdCountForUpdate, householdChunkSize), range =>
            {
                for (var h = range.Item1; h < range.Item2; h++)
                {
                    float totalLog = 0f;
                    int count = 0;

                    for (int i = 0; i < constraintData.Length; i++)
                    {
                        ref var logRatiosReference = ref MemoryMarshal.GetArrayDataReference(constraintData[i].LogRatios);
                        var logRatio = Unsafe.Add(ref logRatiosReference, h);
                        totalLog += logRatio;
                        count += BoolToInt(logRatio != 0.0f);
                    }

                    if (count > 0)
                    {
                        currentExpansionFactors[h] *= MathF.Exp(damping * totalLog / count);
                    }
                }
            });
        }
        log?.Invoke($"Reweighting process completed in {Math.Min(iteration + 1, maxIterations)} iterations of {maxIterations}.");

        // Step 6 write out the final expansion factors to a CSV file
        log?.Invoke($"Writing out final expansion factors...");

        using var writer = new StreamWriter(UpdatedExpansionFactorsFile.Invoke());
        writer.WriteLine("UpdatedExpansionFactor");
        for (nuint i = 0; i < (nuint)currentExpansionFactors.Length; i++)
        {
            writer.WriteLine(currentExpansionFactors[i]);
        }

        if (ConstraintReportFile is not null)
        {
            log?.Invoke("Writing out constraint report...");
            WriteConstraintReport(ConstraintReportFile.Invoke(), constraints, constraintData, surveyRecords, currentExpansionFactors);
        }

        log?.Invoke($"Finished Reweighting");
    }

    /// <summary>
    /// Converts a boolean value to an integer (1 for true, 0 for false).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int BoolToInt(bool condition)
    {
        return (int)Unsafe.As<bool, byte>(ref condition);
    }

    private void WriteConstraintReport(
        Stream stream,
        Constraints constraints,
        ConstraintData[] constraintData,
        SurveyRecords surveyRecords,
        float[] expansionFactors)
    {
        using var writer = new StreamWriter(stream);
        writer.WriteLine("ConstraintType,Constraint,Geography,Target,Actual,AbsoluteDifference,Ratio");

        WriteConstraintReports(writer, "Household", constraints.HouseholdConstraints, constraintData, 0,
            surveyRecords.HouseholdData, surveyRecords.HouseholdAttributes.Length, expansionFactors);
        WriteConstraintReports(writer, "Person", constraints.PersonConstraints, constraintData, constraints.HouseholdConstraints.Length,
            surveyRecords.PersonData, surveyRecords.PersonAttributes.Length, expansionFactors);
        WriteConstraintReports(writer, "Trip", constraints.TripConstraints, constraintData, constraints.HouseholdConstraints.Length + constraints.PersonConstraints.Length,
            surveyRecords.TripData, surveyRecords.TripAttributes.Length, expansionFactors);
    }

    private static void WriteConstraintReports(
        StreamWriter writer,
        string constraintType,
        Constraint[] constraints,
        ConstraintData[] constraintData,
        int dataOffset,
        float[] data,
        int numberOfAttributes,
        float[] expansionFactors)
    {
        for (int i = 0; i < constraints.Length; i++)
        {
            var constraint = constraints[i];
            var dataInfo = constraintData[dataOffset + i];
            var actuals = constraint.Aggregate(data, numberOfAttributes, expansionFactors, dataInfo.ExpansionIndex, dataInfo.GeographyIndex);
            var targetTotal = constraint.Targets.Sum();
            var actualTotal = actuals.Sum();

            for (int geography = 0; geography < constraint.Targets.Length; geography++)
            {
                WriteConstraintReportRow(writer, constraintType, constraint.Name,
                    constraint.CategoryMap?.Destination.GetSparseIndex(geography) ?? geography,
                    constraint.Targets[geography], actuals[geography]);
            }

            WriteConstraintReportRow(writer, constraintType, constraint.Name, new CategoryIndex(-1), targetTotal, actualTotal);
        }
    }

    private static void WriteConstraintReportRow(
        StreamWriter writer,
        string constraintType,
        string constraintName,
        CategoryIndex geography,
        float target,
        float actual)
    {
        var difference = actual - target;
        var ratio = target == 0.0f ? 1.0f : actual / target;
        writer.WriteLine($"{CsvEscape(constraintType)},{CsvEscape(constraintName)},{CsvEscape(geography.Exists ? ((long)(nint)geography).ToString() : "Global")},{target},{actual},{difference},{ratio}");
    }

    private static string CsvEscape(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

}

/// <summary>
/// Computes the per-constraint corrections (log-ratios) for each household using the expansion factors and survey records.
/// </summary>
/// <param name="module">The module that is calling this method</param>
/// <param name="constraints">The constraints to be computed.</param>
/// <param name="surveyRecords">The survey records to use for the computation.</param>
/// <param name="currentExpansionFactors">The current expansion factors</param>
/// <param name="logRatios">The resulting log-ratios for each household.</param>
internal sealed class ConstraintData
{
    private readonly IModule _module;

    public ConstraintData(IModule module, Constraint constraint, int recordCount, nuint[]? expansionIndex)
    {
        _module = module;
        Constraint = constraint;
        Scratch = new float[constraint.Targets.Length];
        GeographyIndex = new nuint[recordCount];

        if (expansionIndex is null)
        {
            expansionIndex = new nuint[recordCount];
            for (int i = 0; i < recordCount; i++)
            {
                expansionIndex[i] = (nuint)i;
            }
        }
        ExpansionIndex = expansionIndex;
        LogRatios = new float[ExpansionIndex.Length];
    }

    public Constraint Constraint { get; }

    public float[] Scratch { get; }

    public float[] LogRatios { get; }

    public nuint[] ExpansionIndex { get; }

    public nuint[] GeographyIndex { get; }

    public void InitializeGeographyIndexes(CategoryMap? map, Func<nuint, CategoryIndex> getRecordTAZ)
    {
        if (map is null)
        {
            for (int j = 0; j < GeographyIndex.Length; j++)
            {
                GeographyIndex[j] = (nuint)getRecordTAZ((nuint)j);
            }
            return;
        }

        var lookup = map.CreateIndex();
        for (int j = 0; j < GeographyIndex.Length; j++)
        {
            var recordTAZ = getRecordTAZ((nuint)j);
            if (!lookup.TryGetValue(recordTAZ, out var mappedGeography))
            {
                throw new XTMFRuntimeException(_module, $"Record {j} TAZ {recordTAZ} could not be mapped to the constraint {Constraint.Name}'s geography.");
            }
            GeographyIndex[j] = (nuint)mappedGeography;
        }
    }
}
