namespace TMG.SurveyReweight;

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

    [Parameter(DefaultValue = "1.0", Name = "ConstraintWeight", Index = 7, Description = "The relative weight of this constraint in the calibration objective. Set to zero to exclude it from optimization.")]
    public IFunction<float> ConstraintWeight = null!;

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
        var constraintWeight = ConstraintWeight.Invoke();

        if (geographyColumn < 0 || targetColumn < 0)
        {
            throw new XTMFRuntimeException(this, "CSV column indexes must be greater than or equal to zero.");
        }
        if (!float.IsFinite(constraintWeight) || constraintWeight < 0.0f)
        {
            throw new XTMFRuntimeException(this, "ConstraintWeight must be finite and greater than or equal to zero.");
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

        return new Constraint(this, constraintName, categoryMap, matchingAttributes, targets, Tolerance.Invoke(), constraintWeight)
        {
            GeographyName = CategoryMap is null ? "TAZ" : CategoryMap.Name ?? "Unnamed Geography"
        };
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
/// <param name="Weight">The relative contribution of the constraint to the calibration objective.</param>
public record Constraint(
    IModule Module,
    string Name,
    CategoryMap? CategoryMap,
    RecordAttribute[] MatchingAttributes,
    float[] Targets,
    float Tolerance,
    float Weight = 1.0f)
{
    public string GeographyName { get; init; } = "TAZ";

    nuint[] _matchedIndexes = [.. MatchingAttributes.Select(a => (nuint)a.Index)];
    private const int ParallelWorkThreshold = 128;

    internal float[] Aggregate(float[] data, int numberOfAttributes, float[] currentExpansionFactors, nuint[] expIndexes, nuint[] geographyIndex)
    {
        var actuals = new float[Targets.Length];
        var numberOfRecords = expIndexes.Length;
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

    internal ConstraintContributions CreateContributions(
        float[] data,
        nuint numberOfAttributes,
        nuint[] expIndexes,
        nuint[] geographyIndex,
        int householdCount)
    {
        var matchedValues = new float[expIndexes.Length];
        Parallel.For(0, expIndexes.Length, i =>
        {
            var matchedValue = 1.0f;
            for (nuint j = 0; j < (nuint)_matchedIndexes.Length; j++)
            {
                matchedValue *= data[(nuint)i * numberOfAttributes + _matchedIndexes[j]];
            }
            matchedValues[i] = matchedValue;
        });

        var recordContributions = new List<RecordContribution>(expIndexes.Length);
        for (int i = 0; i < expIndexes.Length; i++)
        {
            if (matchedValues[i] != 0.0f)
            {
                recordContributions.Add(new RecordContribution(
                    (int)geographyIndex[i], (int)expIndexes[i], i, matchedValues[i]));
            }
        }

        recordContributions.Sort(static (left, right) =>
        {
            var result = left.GeographyIndex.CompareTo(right.GeographyIndex);
            if (result == 0)
            {
                result = left.HouseholdIndex.CompareTo(right.HouseholdIndex);
            }
            return result == 0 ? left.RecordIndex.CompareTo(right.RecordIndex) : result;
        });

        var pairContributions = new List<PairContribution>(recordContributions.Count);
        for (int start = 0; start < recordContributions.Count;)
        {
            var first = recordContributions[start];
            var value = 0.0;
            var next = start;
            while (next < recordContributions.Count &&
                   recordContributions[next].GeographyIndex == first.GeographyIndex &&
                   recordContributions[next].HouseholdIndex == first.HouseholdIndex)
            {
                value += recordContributions[next].Value;
                next++;
            }

            if (value != 0.0)
            {
                pairContributions.Add(new PairContribution(first.GeographyIndex, first.HouseholdIndex, value));
            }
            start = next;
        }

        var pairGeographies = new int[pairContributions.Count];
        var pairHouseholds = new int[pairContributions.Count];
        var pairValues = new double[pairContributions.Count];
        var geographyOffsets = new int[Targets.Length + 1];
        var householdOffsets = new int[householdCount + 1];
        for (int i = 0; i < pairContributions.Count; i++)
        {
            var pair = pairContributions[i];
            pairGeographies[i] = pair.GeographyIndex;
            pairHouseholds[i] = pair.HouseholdIndex;
            pairValues[i] = pair.Value;
            geographyOffsets[pair.GeographyIndex + 1]++;
            householdOffsets[pair.HouseholdIndex + 1]++;
        }

        for (int i = 1; i < geographyOffsets.Length; i++)
        {
            geographyOffsets[i] += geographyOffsets[i - 1];
        }
        for (int i = 1; i < householdOffsets.Length; i++)
        {
            householdOffsets[i] += householdOffsets[i - 1];
        }

        var householdPairIndexes = Enumerable.Range(0, pairContributions.Count).ToArray();
        Array.Sort(householdPairIndexes, (left, right) =>
        {
            var result = pairHouseholds[left].CompareTo(pairHouseholds[right]);
            return result == 0
                ? pairGeographies[left].CompareTo(pairGeographies[right])
                : result;
        });

        return new ConstraintContributions(
            geographyOffsets, householdOffsets, pairGeographies,
            pairHouseholds, pairValues, householdPairIndexes);
    }

    internal bool AccumulateObjectiveAndGradient(
        ConstraintContributions contributions,
        float[] expansionFactors,
        double[] actuals,
        double[]? residualGradient,
        double[]? householdGradient,
        int totalConstraintCells,
        out double objective)
    {
        if (!float.IsFinite(Weight) || Weight < 0.0f)
        {
            throw new XTMFRuntimeException(Module, $"Constraint {Name} has a weight that is not finite and nonnegative.");
        }

        Array.Clear(actuals);
        Action<int> aggregateGeography = geography =>
        {
            var actual = 0.0;
            for (int i = contributions.GeographyOffsets[geography]; i < contributions.GeographyOffsets[geography + 1]; i++)
            {
                var pairIndex = i;
                actual += expansionFactors[contributions.PairHouseholds[pairIndex]] *
                    contributions.PairValues[pairIndex];
            }
            actuals[geography] = actual;
        };
        if (Targets.Length >= ParallelWorkThreshold)
        {
            Parallel.For(0, Targets.Length, aggregateGeography);
        }
        else
        {
            for (int geography = 0; geography < Targets.Length; geography++)
            {
                aggregateGeography(geography);
            }
        }

        var withinTolerance = true;
        objective = 0.0;
        var cellCount = Math.Max(1, totalConstraintCells);
        for (int i = 0; i < Targets.Length; i++)
        {
            var target = Targets[i];
            if (!float.IsFinite(target) || target < 0.0f)
            {
                throw new XTMFRuntimeException(Module, $"Constraint {Name} has a target that is not finite and nonnegative.");
            }

            var actual = actuals[i];
            var difference = actual - target;
            var scale = Math.Max(Math.Abs((double)target), 1.0);
            var scaledDifference = difference / scale;
            objective += Weight * 0.5 * scaledDifference * scaledDifference / cellCount;
            if (residualGradient is not null)
            {
                residualGradient[i] = Weight * difference / (scale * scale * cellCount);
            }

            if (Weight > 0.0f && target > 0.0f && actual <= 0.0)
            {
                var sparseGeography = (long)(CategoryMap?.Destination.GetSparseIndex(i) ?? i);
                throw new XTMFRuntimeException(Module,
                    $"Constraint {Name} is not satisfied because the current expansion factor for geography {sparseGeography} is zero. " +
                    $"This means no survey records aggregate to target geography {sparseGeography} (of {Targets.Length} geographies). " +
                    "If this happens on the first iteration, re-check the CategoryMap: its Base must be the TAZs and its Destination the constraint geography — a reversed or mismatched map will silently route records to the wrong geographies.");
            }

            var ratio = target == 0.0f
                ? (actual <= 0.0 ? 1.0 : 0.0)
                : target / actual;
            if (Math.Abs(ratio - 1.0) > Tolerance)
            {
                withinTolerance = false;
            }
        }

        if (householdGradient is not null && residualGradient is not null)
        {
            Action<int> aggregateHousehold = household =>
            {
                var gradient = 0.0;
                for (int i = contributions.HouseholdOffsets[household]; i < contributions.HouseholdOffsets[household + 1]; i++)
                {
                    var pairIndex = contributions.HouseholdPairIndexes[i];
                    gradient += contributions.PairValues[pairIndex] *
                        residualGradient[contributions.PairGeographies[pairIndex]];
                }
                householdGradient[household] = gradient;
            };
            if (householdGradient.Length >= ParallelWorkThreshold)
            {
                Parallel.For(0, householdGradient.Length, aggregateHousehold);
            }
            else
            {
                for (int household = 0; household < householdGradient.Length; household++)
                {
                    aggregateHousehold(household);
                }
            }
        }

        return withinTolerance;
    }

    internal double EvaluateConstraintObjective(
        ConstraintContributions contributions,
        float[] expansionFactors,
        double[] actuals,
        int totalConstraintCells,
        out bool withinTolerance)
    {
        withinTolerance = AccumulateObjectiveAndGradient(
            contributions, expansionFactors, actuals, null, null,
            totalConstraintCells, out var objective);
        return objective;
    }

    private readonly record struct RecordContribution(int GeographyIndex, int HouseholdIndex, int RecordIndex, double Value);

    private readonly record struct PairContribution(int GeographyIndex, int HouseholdIndex, double Value);
}

internal sealed class ConstraintContributions(
    int[] geographyOffsets,
    int[] householdOffsets,
    int[] pairGeographies,
    int[] pairHouseholds,
    double[] pairValues,
    int[] householdPairIndexes)
{
    public int[] GeographyOffsets { get; } = geographyOffsets;

    public int[] HouseholdOffsets { get; } = householdOffsets;

    public int[] PairGeographies { get; } = pairGeographies;

    public int[] PairHouseholds { get; } = pairHouseholds;

    public double[] PairValues { get; } = pairValues;

    public int[] HouseholdPairIndexes { get; } = householdPairIndexes;
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
