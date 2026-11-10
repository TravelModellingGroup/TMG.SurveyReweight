using XTMF2.RuntimeModules;
using System.Numerics;
using TMG.Utilities;

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

    [Parameter(Name = "Damping", DefaultValue = "1.0", Description = "Scales gradient steps relative to the configured expansion factor range; backtracking reduces steps as needed.", Index = 4)]
    public IFunction<float> Damping = null!;

    [Parameter(Name = "MinimumExpansionFactor", DefaultValue = "1.0", Description = "The minimum allowed expansion factor during the reweighting process.", Index = 5)]
    public IFunction<float> MinimumExpansionFactor = null!;

    [Parameter(Name = "MaximumExpansionFactor", DefaultValue = "50.0", Description = "The maximum allowed expansion factor during the reweighting process.", Index = 6)]
    public IFunction<float> MaximumExpansionFactor = null!;

    [Parameter(Name = "KLDivergencePenalty", DefaultValue = "1.0", Description = "The relative weight of the KL-divergence penalty in the calibration objective.  A value of 1.0 indicates full weight, a value of 0 removes it.", Index = 10)]
    public IFunction<float> KLDivergencePenalty = null!;

    [SubModule(Required = true, Name = "UpdatedExpansionFactorsFile", Description = "The CSV file to write the updated expansion factors to.", Index = 7)]
    public IFunction<WriteStream> UpdatedExpansionFactorsFile = null!;

    [SubModule(Required = false, Name = "Log", Description = "The log for the reweighting process.", Index = 8)]
    public IFunction<Log>? Log;

    [SubModule(Required = false, Name = "ConstraintReportFile", Description = "The optional CSV file to write the final constraint report to.", Index = 9)]
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
        if ((nuint)surveyRecords.InitialExpansionFactors.Length != surveyRecords.NumberOfHouseholds)
        {
            throw new XTMFRuntimeException(this,
                $"The survey records contain {surveyRecords.InitialExpansionFactors.Length} initial expansion factors for {surveyRecords.NumberOfHouseholds} households.");
        }

        var minimumExpansionFactor = MinimumExpansionFactor.Invoke();
        var maximumExpansionFactor = MaximumExpansionFactor.Invoke();
        if (!float.IsFinite(minimumExpansionFactor) || !float.IsFinite(maximumExpansionFactor) ||
            minimumExpansionFactor <= 0.0f || maximumExpansionFactor < minimumExpansionFactor)
        {
            throw new XTMFRuntimeException(this,
                $"The configured expansion factor range [{minimumExpansionFactor}, {maximumExpansionFactor}] must be finite, positive, and ordered.");
        }

        for (int i = 0; i < surveyRecords.InitialExpansionFactors.Length; i++)
        {
            var expansionFactor = surveyRecords.InitialExpansionFactors[i];
            if (!float.IsFinite(expansionFactor) || expansionFactor <= 0.0f)
            {
                throw new XTMFRuntimeException(this, $"The initial expansion factor for household {i} must be finite and greater than zero.");
            }
            if (expansionFactor < minimumExpansionFactor || expansionFactor > maximumExpansionFactor)
            {
                throw new XTMFRuntimeException(this,
                    $"The initial expansion factor {expansionFactor} for household {i} is outside the configured range [{minimumExpansionFactor}, {maximumExpansionFactor}].");
            }
        }

        float[] currentExpansionFactors = (float[])surveyRecords.InitialExpansionFactors.Clone();

        var householdCount = constraints.HouseholdConstraints.Length;
        var personCount = constraints.PersonConstraints.Length;
        var tripCount = constraints.TripConstraints.Length;

        var constraintData = new ConstraintData[householdCount + personCount + tripCount];
        int index = 0;
        foreach (var constraint in constraints.HouseholdConstraints)
        {
            constraintData[index++] = new ConstraintData(this, constraint,
                surveyRecords.HouseholdData, surveyRecords.HouseholdAttributes.Length,
                (int)surveyRecords.NumberOfHouseholds, null);
        }
        foreach (var constraint in constraints.PersonConstraints)
        {
            constraintData[index++] = new ConstraintData(this, constraint,
                surveyRecords.PersonData, surveyRecords.PersonAttributes.Length,
                (int)surveyRecords.NumberOfPersons, surveyRecords.PersonHouseholdIndex);
        }
        foreach (var constraint in constraints.TripConstraints)
        {
            constraintData[index++] = new ConstraintData(this, constraint,
                surveyRecords.TripData, surveyRecords.TripAttributes.Length,
                (int)surveyRecords.NumberOfTrips, surveyRecords.TripHouseholdIndex);
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
        foreach (var data in constraintData)
        {
            data.InitializeSparseContributions(currentExpansionFactors.Length);
        }


        var klDivergencePenalty = KLDivergencePenalty.Invoke();
        if (!float.IsFinite(damping) || damping <= 0.0f)
        {
            throw new XTMFRuntimeException(this, "Damping must be finite and greater than zero.");
        }
        if (!float.IsFinite(klDivergencePenalty) || klDivergencePenalty < 0.0f)
        {
            throw new XTMFRuntimeException(this, "KLDivergencePenalty must be finite and greater than or equal to zero.");
        }

        var totalConstraintCells = constraintData.Sum(data => data.Constraint.Targets.Length);
        var initialWeightTotal = surveyRecords.InitialExpansionFactors.Sum(weight => (double)weight);
        var gradient = new double[currentExpansionFactors.Length];
        var constraintGradient = new double[currentExpansionFactors.Length];
        var candidateExpansionFactors = new float[currentExpansionFactors.Length];

        // Step 5 minimize relative constraint error plus the KL divergence from the initial weights.
        var currentObjective = EvaluateObjective(
            constraintData, currentExpansionFactors,
            surveyRecords.InitialExpansionFactors, initialWeightTotal, totalConstraintCells,
            klDivergencePenalty, gradient, constraintGradient, out _);
        if (!double.IsFinite(currentObjective))
        {
            throw new XTMFRuntimeException(this, "The initial calibration objective is not finite.");
        }

        var completedIterations = 0;
        var converged = false;
        var expansionFactorStepScale = Math.Max(1.0, maximumExpansionFactor - minimumExpansionFactor);
        var stepSize = (double)damping;
        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            log?.Invoke($"Minimizing calibration objective in iteration {iteration + 1} of {maxIterations}...");
            var maximumGradient = gradient.Length == 0 ? 0.0 : gradient.Max(value => Math.Abs(value));
            if (maximumGradient == 0.0)
            {
                converged = true;
                break;
            }

            var accepted = false;
            var convergedAtCurrentWeights = false;
            double candidateObjective = currentObjective;
            double maximumChange = 0.0;

            for (int lineSearchIteration = 0; lineSearchIteration < 40; lineSearchIteration++)
            {
                var gradientDotDirection = 0.0;
                maximumChange = 0.0;
                for (int h = 0; h < currentExpansionFactors.Length; h++)
                {
                    var normalizedGradient = gradient[h] / maximumGradient;
                    var proposed = currentExpansionFactors[h] -
                        stepSize * expansionFactorStepScale * normalizedGradient;
                    var bounded = Math.Clamp(proposed, minimumExpansionFactor, maximumExpansionFactor);
                    candidateExpansionFactors[h] = (float)bounded;
                    var direction = candidateExpansionFactors[h] - currentExpansionFactors[h];
                    gradientDotDirection += gradient[h] * direction;
                    maximumChange = Math.Max(maximumChange, Math.Abs(direction));
                }

                if (maximumChange == 0.0)
                {
                    convergedAtCurrentWeights = true;
                    break;
                }

                candidateObjective = EvaluateObjective(
                    constraintData, candidateExpansionFactors,
                    surveyRecords.InitialExpansionFactors, initialWeightTotal, totalConstraintCells,
                    klDivergencePenalty, null, constraintGradient, out _);

                if (double.IsFinite(candidateObjective) &&
                    candidateObjective <= currentObjective + 1e-4 * gradientDotDirection)
                {
                    accepted = true;
                    break;
                }

                stepSize *= 0.5;
            }

            if (convergedAtCurrentWeights)
            {
                converged = true;
                break;
            }
            if (!accepted)
            {
                throw new XTMFRuntimeException(this,
                    $"The calibration objective could not find a decreasing step after {completedIterations} iterations.");
            }

            Array.Copy(candidateExpansionFactors, currentExpansionFactors, currentExpansionFactors.Length);
            var objectiveImprovement = currentObjective - candidateObjective;
            currentObjective = candidateObjective;
            completedIterations++;
            EvaluateObjective(
                constraintData, currentExpansionFactors,
                surveyRecords.InitialExpansionFactors, initialWeightTotal, totalConstraintCells,
                klDivergencePenalty, gradient, constraintGradient, out _);

            var maximumCurrentWeight = currentExpansionFactors.Length == 0
                ? 1.0
                : currentExpansionFactors.Max(weight => (double)weight);
            if (maximumChange <= 1e-7 * Math.Max(1.0, maximumCurrentWeight) &&
                objectiveImprovement <= 1e-10 * Math.Max(1.0, currentObjective))
            {
                converged = true;
                break;
            }
        }

        var finalObjective = EvaluateObjective(
            constraintData, currentExpansionFactors,
            surveyRecords.InitialExpansionFactors, initialWeightTotal, totalConstraintCells,
            klDivergencePenalty, null, constraintGradient, out var allConstraintsSatisfied);
        log?.Invoke($"Calibration objective {finalObjective:G8} after {completedIterations} iterations of {maxIterations}; " +
            $"all constraints within tolerance: {allConstraintsSatisfied}; converged: {converged}.");

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

    private double EvaluateObjective(
        ConstraintData[] constraintData,
        float[] expansionFactors,
        float[] initialExpansionFactors,
        double initialWeightTotal,
        int totalConstraintCells,
        double klDivergencePenalty,
        double[]? householdGradient,
        double[] constraintGradient,
        out bool allConstraintsSatisfied)
    {
        if (householdGradient is not null)
        {
            Array.Clear(householdGradient);
        }

        allConstraintsSatisfied = true;
        var objective = 0.0;
        foreach (var data in constraintData)
        {
            if (householdGradient is null)
            {
                data.Objective = data.Constraint.EvaluateConstraintObjective(
                    data.Contributions, expansionFactors, data.Actuals,
                    totalConstraintCells, out var withinTolerance);
                data.WithinTolerance = withinTolerance;
            }
            else
            {
                Array.Clear(constraintGradient);
                data.WithinTolerance = data.Constraint.AccumulateObjectiveAndGradient(
                    data.Contributions, expansionFactors, data.Actuals,
                    data.ResidualGradient, constraintGradient,
                    totalConstraintCells, out var constraintObjective);
                data.Objective = constraintObjective;
                AddGradient(householdGradient, constraintGradient);
            }

            objective += data.Objective;
            allConstraintsSatisfied &= data.WithinTolerance;
        }

        var divergence = 0.0;
        var divergenceScale = Math.Max(initialWeightTotal, double.Epsilon);
        var factor = klDivergencePenalty / divergenceScale;
        for (int h = 0; h < expansionFactors.Length; h++)
        {
            var logRatio = Math.Log(expansionFactors[h] / initialExpansionFactors[h]);
            var weight = expansionFactors[h];
            var initialWeight = initialExpansionFactors[h];
            var term = weight * logRatio - weight + initialWeight;
            divergence += Math.Max(0.0, term);
        }

        objective += klDivergencePenalty * divergence / divergenceScale;
        if (householdGradient is not null)
        {
            Parallel.For(0, expansionFactors.Length, h =>
            {
                var logRatio = Math.Log(expansionFactors[h] / initialExpansionFactors[h]);
                householdGradient[h] += factor * logRatio;
            });
        }

        if (!double.IsFinite(objective) ||
            (householdGradient is not null && householdGradient.Any(value => !double.IsFinite(value))))
        {
            throw new XTMFRuntimeException(this, "The calibration objective or its gradient is not finite.");
        }

        return objective;
    }

    private static void AddGradient(double[] gradient, double[] constraintGradient)
    {
        var index = 0;
        var vectorWidth = Vector<double>.Count;
        for (; index <= gradient.Length - vectorWidth; index += vectorWidth)
        {
            var gradientVector = new Vector<double>(gradient, index);
            var constraintVector = new Vector<double>(constraintGradient, index);
            (gradientVector + constraintVector).CopyTo(gradient, index);
        }

        for (; index < gradient.Length; index++)
        {
            gradient[index] += constraintGradient[index];
        }
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

internal sealed class ConstraintData
{
    private readonly IModule _module;

    public ConstraintData(
        IModule module,
        Constraint constraint,
        float[] recordData,
        int numberOfAttributes,
        int recordCount,
        nuint[]? expansionIndex)
    {
        _module = module;
        Constraint = constraint;
        RecordData = recordData;
        NumberOfAttributes = (nuint)numberOfAttributes;
        Actuals = new double[constraint.Targets.Length];
        ResidualGradient = new double[constraint.Targets.Length];
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
    }

    public Constraint Constraint { get; }

    public float[] RecordData { get; }

    public nuint NumberOfAttributes { get; }

    public double[] Actuals { get; }

    public double[] ResidualGradient { get; }

    public double Objective { get; set; }

    public bool WithinTolerance { get; set; }

    public nuint[] ExpansionIndex { get; }

    public nuint[] GeographyIndex { get; }

    public ConstraintContributions Contributions { get; private set; } = null!;

    public void InitializeSparseContributions(int householdCount)
    {
        Contributions = Constraint.CreateContributions(
            RecordData, NumberOfAttributes, ExpansionIndex, GeographyIndex, householdCount);
    }

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
