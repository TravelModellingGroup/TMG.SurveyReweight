namespace TMG.SurveyReweight;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TMG.Utilities;

[Module(
    Name = "LoadConstraint",
    Description = "Loads a constraint for the reweighting process.",
    DocumentationLink = "https://tmg.utoronto.ca/doc/2.0/"
)]
public class LoadConstraint : BaseFunction<(ZoneSystem ZoneSystem, RecordAttribute[] AvailableAttributes), Constraint>
{
    [SubModule(Required = true, Name = "File", Description = "The CSV file containing the constraint targets.", Index = 0)]
    public IFunction<ReadStream> File = null!;

    [SubModule(Required = false, Name = "MatchingAttributes", Description = "The survey record attributes to match for this constraint.", Index = 1)]
    public IFunction<string>[] MatchingAttributes = null!;

    [SubModule(Required = false, Name = "CategoryMap", Description = "The optional map from TAZs to the constraint geography.", Index = 2)]
    public IFunction<CategoryMap>? CategoryMap;

    [Parameter(DefaultValue = "0.01", Name = "Tolerance", Index = 3, Description = "The tolerance used to determine whether the constraint is satisfied.")]
    public IFunction<float> Tolerance = null!;

    [Parameter(DefaultValue = "0", Name = "GeographyColumn", Index = 4, Description = "The zero-based CSV column containing the sparse geography index.")]
    public IFunction<int> GeographyColumn = null!;

    [Parameter(DefaultValue = "1", Name = "TargetColumn", Index = 5, Description = "The zero-based CSV column containing the target value.")]
    public IFunction<int> TargetColumn = null!;

    [SubModule(Required = false, Name = "Constraint Name", Description = "The name of the constraint.", Index = 6)]
    public IFunction<string>? ConstraintName = null;

    private RecordAttribute[] GetMatchingAttributes(string constraintName, RecordAttribute[] availableAttributes)
    {
        var matchingAttributes = new RecordAttribute[MatchingAttributes.Length];
        for (int i = 0; i < MatchingAttributes.Length; i++)
        {
            var attributeName = MatchingAttributes[i].Invoke();
            var matchingAttribute = availableAttributes.FirstOrDefault(attribute => attribute is not null && attribute.Name == attributeName);
            if (matchingAttribute is not null)
            {
                matchingAttributes[i] = matchingAttribute;
                continue;
            }

            throw new XTMFRuntimeException(this, $"No matching attribute found for constraint {constraintName}: {attributeName}. Potential options were: {string.Join(", ", availableAttributes.Select(a => a?.Name ?? "Unnamed Attribute"))}");
        }
        return matchingAttributes;
    }

    public override Constraint Invoke((ZoneSystem ZoneSystem, RecordAttribute[] AvailableAttributes) context)
    {
        var zoneSystem = context.ZoneSystem;
        var constraintName = ConstraintName?.Invoke() ?? this.Name ?? "Unnamed Constraint";
        var matchingAttributes = GetMatchingAttributes(constraintName, context.AvailableAttributes);
        var geographyColumn = GeographyColumn.Invoke();
        var targetColumn = TargetColumn.Invoke();

        if (geographyColumn < 0 || targetColumn < 0)
        {
            throw new XTMFRuntimeException(this, "CSV column indexes must be greater than or equal to zero.");
        }
        if (geographyColumn == targetColumn)
        {
            throw new XTMFRuntimeException(this, "The geography and target columns must be different.");
        }

        var categoryMap = CategoryMap?.Invoke();
        if (categoryMap is not null && !categoryMap.Base.SequenceEqual(zoneSystem.TAZ))
        {
            throw new XTMFRuntimeException(this,
                $"The CategoryMap for constraint {constraintName} is not aligned with the zone system TAZs: the map's Base contains {categoryMap.Base.Count} categories while the zone system has {zoneSystem.TAZ.Count} TAZs, or the sets do not match exactly. " +
                "The CategoryMap's BaseCategories must contain the same TAZs as the zone system; verify that Base is the TAZs and Destination is the constraint geography (and that they are not reversed).");
        }
        var categoryLookup = categoryMap?.Destination ?? zoneSystem.TAZ;
        var targets = new float[categoryMap?.Destination.Count ?? zoneSystem.TAZ.Count];
        var loaded = new bool[targets.Length];
        var minimumColumns = Math.Max(geographyColumn, targetColumn);

        using var reader = new CsvReader(File.Invoke(), true);
        try
        {
            while (reader.LoadLine(out var columns))
            {
                // Skip blank columns
                if (columns <= 0)
                {
                    continue;
                }

                if (columns <= minimumColumns)
                {
                    throw new XTMFRuntimeException(this, $"The constraint file {reader.FileName} has too few columns on line {reader.LineNumber}.");
                }

                reader.Get(out int sparseGeography, geographyColumn);
                reader.Get(out float target, targetColumn);

                int flatGeography = categoryLookup.GetFlatIndex((CategoryIndex)sparseGeography);
                
                if (flatGeography < 0)
                {
                    throw new XTMFRuntimeException(this, $"The geography index {sparseGeography} on line {reader.LineNumber} could not be mapped for constraint {constraintName}.");
                }
                
                if (loaded[flatGeography])
                {
                    throw new XTMFRuntimeException(this, $"The geography index {sparseGeography} is specified more than once on line {reader.LineNumber}.");
                }

                targets[flatGeography] = target;
                loaded[flatGeography] = true;
            }
        }
        catch (XTMFRuntimeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new XTMFRuntimeException(this, $"Unable to read from {reader.FileName} on line {reader.LineNumber}: {exception.Message}");
        }

        return new Constraint(this, constraintName, categoryMap, matchingAttributes, targets, Tolerance.Invoke());
    }
}

[Module(
    Name = "LoadConstraints", 
    Description = "Loads the constraints for the reweighting process.",
    DocumentationLink = "https://tmg.utoronto.ca/doc/2.0/"
)]
public class LoadConstraints : BaseFunction<(ZoneSystem ZoneSystem, SurveyRecords SurveyRecords), Constraints>
{
    [SubModule(Required = false, Name = "HouseholdConstraints", Description = "The household constraints to use for the reweighting process.", Index = 0)]
    public IFunction<(ZoneSystem, RecordAttribute[]), Constraint>[] HouseholdConstraints = null!;

    [SubModule(Required = false, Name = "PersonConstraints", Description = "The person constraints to use for the reweighting process.", Index = 1)]
    public IFunction<(ZoneSystem, RecordAttribute[]), Constraint>[] PersonConstraints = null!;

    [SubModule(Required = false, Name = "TripConstraints", Description = "The trip constraints to use for the reweighting process.", Index = 2)]
    public IFunction<(ZoneSystem, RecordAttribute[]), Constraint>[] TripConstraints = null!;

    public override Constraints Invoke((ZoneSystem ZoneSystem, SurveyRecords SurveyRecords) context)
    {
        var zoneSystem = context.ZoneSystem;
        var surveyRecords = context.SurveyRecords;
        var household = new Constraint[HouseholdConstraints.Length];
        var person = new Constraint[PersonConstraints.Length];
        var trip = new Constraint[TripConstraints.Length];

        Parallel.Invoke(
            () => Parallel.For(0, HouseholdConstraints.Length, i =>
                household[i] = HouseholdConstraints[i].Invoke((zoneSystem, surveyRecords.HouseholdAttributes))),
            () => Parallel.For(0, PersonConstraints.Length, i =>
                person[i] = PersonConstraints[i].Invoke((zoneSystem, surveyRecords.PersonAttributes))),
            () => Parallel.For(0, TripConstraints.Length, i =>
                trip[i] = TripConstraints[i].Invoke((zoneSystem, surveyRecords.TripAttributes))));

        return new Constraints
        {
            HouseholdConstraints = household,
            PersonConstraints = person,
            TripConstraints = trip
        };
    }
}

/// <summary>
/// Provides a constraint for the reweighting process.
/// </summary>
/// <param name="Module">The module that created the constraint</param>
/// <param name="Name">The name of the constraint</param>
/// <param name="CategoryMap">The aggregation of geography to use, leave null for TAZ.</param>
/// <param name="MatchingAttributes">The survey record attributes to bind to.</param>
/// <param name="Targets">The target values for the constraint.</param>
public record Constraint(IModule Module, string Name, CategoryMap? CategoryMap, RecordAttribute[] MatchingAttributes, float[] Targets, float Tolerance)
{

    nuint[] _matchedIndexes = [.. MatchingAttributes.Select(a => (nuint)a.Index)];
    float[]? _matchedValues;

    internal float[] Aggregate(float[] data, int numberOfAttributes, float[] currentExpansionFactors, nuint[] expIndexes, nuint[] geographyIndex)
    {
        var actuals = new float[Targets.Length];
        var numberOfRecords = data.Length / numberOfAttributes;
        for (int i = 0; i < numberOfRecords; i++)
        {
            var recordTotal = currentExpansionFactors[expIndexes[i]];
            for (int j = 0; j < _matchedIndexes.Length; j++)
            {
                recordTotal *= data[i * numberOfAttributes + (int)_matchedIndexes[j]];
            }
            actuals[geographyIndex[i]] += recordTotal;
        }
        return actuals;
    }

    /// <summary>
    /// Update the constraint using the current expansion factors and provide a new set of expansion factors.
    /// </summary>
    /// <param name="data">The data of the current level</param>
    /// <param name="currentExpansionFactors">The current expansion factors (does not write to this)</param>
    /// <param name="scratch">Temporary scratch space for calculations</param>
    /// <param name="expIndexes">The household index of each record (also used for addressing the logRatios).</param>
    /// <param name="geographyIndex">The geography index for each record (used for matching to the constraint's Target geography)</param>
    /// <param name="logRatios">The natural log of the correction ratio per household. A value of zero indicates no correction is needed.</param>
    /// <returns>True if the constraint is satisfied, otherwise false</returns>
    internal bool UpdateConstraint(float[] data, nuint numberOfAttributes, float[] currentExpansionFactors,
         float[] scratch, nuint[] expIndexes, nuint[] geographyIndex, float[] logRatios)
    {
        
        var numberOfRecords = (nuint)(data.Length / (int)numberOfAttributes);

        // Precache the matched values for each record to avoid recalculating them multiple times.
        var matchedValues = _matchedValues;
        if (matchedValues is null)
        {
            // We should only need to clear these on our first run, values that get set will be overwritten, and values that don't will be zeroed out
            // from this first iteration.
            scratch.AsSpan().Clear();
            logRatios.AsSpan().Clear();
            matchedValues = new float[(int)numberOfRecords];
            Parallel.For(0, (int)numberOfRecords, i =>
            {
                var matchedValue = 1.0f;
                for (nuint j = 0; j < (nuint)_matchedIndexes.Length; j++)
                {
                    matchedValue *= data[(nuint)i * numberOfAttributes + _matchedIndexes[j]];
                }
                matchedValues[i] = matchedValue;
            });
            _matchedValues = matchedValues;
        }

        // Step 1 aggregate the current expansion factors to the
        ref var currentExpansionFactorsReference = ref MemoryMarshal.GetArrayDataReference(currentExpansionFactors);
        ref var matchedValuesReference = ref MemoryMarshal.GetArrayDataReference(matchedValues);
        ref var scratchReference = ref MemoryMarshal.GetArrayDataReference(scratch);
        ref var expIndexesReference = ref MemoryMarshal.GetArrayDataReference(expIndexes);
        ref var geographyIndexReference = ref MemoryMarshal.GetArrayDataReference(geographyIndex);
        for (nuint i = 0; i < numberOfRecords; i++)
        {
            // Match the records geography to the constraint's geography
            var expansionIndex = Unsafe.Add(ref expIndexesReference, (nint)i);
            var geographyIndexValue = Unsafe.Add(ref geographyIndexReference, (nint)i);
            var recordExp = Unsafe.Add(ref currentExpansionFactorsReference, (nint)expansionIndex) *
                Unsafe.Add(ref matchedValuesReference, (nint)i);

            ref var scratchValue = ref Unsafe.Add(ref scratchReference, (nint)geographyIndexValue);
            scratchValue += recordExp;
        }

        // Step 2 compare the results to the targets and compute how we need to reweight the expansion factors to meet the targets.
        var withinTolerance = true;
        for (nuint i = 0; i < (nuint)Targets.Length; i++)
        {
            float ratio;
            if (Targets[i] > 0)
            {
                if (scratch[i] <= 0)
                {
                    // If the current expansion factor is zero, we cannot reweight it to meet the target.
                    // This means that the constraint is not satisfied.
                    var sparseGeography = (long)(CategoryMap?.Destination.GetSparseIndex((int)i) ?? (int)i);
                    throw new XTMFRuntimeException(Module,
                        $"Constraint {Name} is not satisfied because the current expansion factor for geography {sparseGeography} is zero. " +
                        $"This means no survey records aggregate to target geography {sparseGeography} (of {Targets.Length} geographies). " +
                        "If this happens on the first iteration, re-check the CategoryMap: its Base must be the TAZs and its Destination the constraint geography — a reversed or mismatched map will silently route records to the wrong geographies.");
                }
                ratio = Targets[i] / scratch[i];
                scratch[i] = ratio;
            }
            else
            {
                ratio = scratch[i] <= 0 ? 1.0f : Targets[i] / scratch[i];
                scratch[i] = ratio;
            }
            // Check to see if the constraint is satisfied within the tolerance
            if (Math.Abs(ratio - 1.0f) > Tolerance)
            {
                // If the ratio is not within the tolerance, the constraint is not satisfied.
                withinTolerance = false;
            }
            scratch[i] = MathF.Log(ratio);
        }

        // Step 3 write the logarithm of the correction ratio to every matched household.
        // Expansion factors are households, so all levels (household, person, trip) address the same array.
        var recordCount = (int)numberOfRecords;
        var chunkSize = Math.Max(1024, recordCount / (Environment.ProcessorCount * 4));
        Parallel.ForEach(Partitioner.Create(0, recordCount, chunkSize), range =>
        {
            for (var i = range.Item1; i < range.Item2; i++)
            {
                if (matchedValues[i] > 0)
                {
                    // At this point scratch contains the logarithm of the correction ratio for each geography.
                    logRatios[expIndexes[i]] = scratch[geographyIndex[i]];
                }
            }
        });

        return withinTolerance;
    }
}

/// <summary>
/// The collection of constraints applied to the reweighting process,
/// grouped by the table they apply to.
/// </summary>
public sealed class Constraints
{
    /// <summary>
    /// The constraints applied to the household table.
    /// </summary>
    public required Constraint[] HouseholdConstraints { get; init; } = [];

    /// <summary>
    /// The constraints applied to the person table.
    /// </summary>
    public required Constraint[] PersonConstraints { get; init; } = [];

    /// <summary>
    /// The constraints applied to the trip table.
    /// </summary>
    public required Constraint[] TripConstraints { get; init; } = [];
}
