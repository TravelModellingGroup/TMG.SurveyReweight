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

        return new Constraint(this, constraintName, categoryMap, matchingAttributes, targets, Tolerance.Invoke(), constraintWeight);
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

    nuint[] _matchedIndexes = [.. MatchingAttributes.Select(a => (nuint)a.Index)];
    float[]? _matchedValues;

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

    internal bool AccumulateObjectiveAndGradient(
        float[] data,
        nuint numberOfAttributes,
        float[] expansionFactors,
        nuint[] expIndexes,
        nuint[] geographyIndex,
        double[] actuals,
        double[]? residualGradient,
        double[]? householdGradient,
        int totalConstraintCells,
        out double objective)
    {
        var numberOfRecords = expIndexes.Length;
        if (!float.IsFinite(Weight) || Weight < 0.0f)
        {
            throw new XTMFRuntimeException(Module, $"Constraint {Name} has a weight that is not finite and nonnegative.");
        }

        var matchedValues = _matchedValues;
        if (matchedValues is null)
        {
            matchedValues = new float[numberOfRecords];
            Parallel.For(0, numberOfRecords, i =>
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

        Array.Clear(actuals);
        for (int i = 0; i < numberOfRecords; i++)
        {
            actuals[geographyIndex[i]] += expansionFactors[expIndexes[i]] * matchedValues[i];
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
            for (int i = 0; i < numberOfRecords; i++)
            {
                householdGradient[expIndexes[i]] += matchedValues[i] * residualGradient[geographyIndex[i]];
            }
        }

        return withinTolerance;
    }

    internal double EvaluateConstraintObjective(
        float[] data,
        nuint numberOfAttributes,
        float[] expansionFactors,
        nuint[] expIndexes,
        nuint[] geographyIndex,
        double[] actuals,
        int totalConstraintCells,
        out bool withinTolerance)
    {
        withinTolerance = AccumulateObjectiveAndGradient(
            data, numberOfAttributes, expansionFactors, expIndexes, geographyIndex,
            actuals, null, null, totalConstraintCells, out var objective);
        return objective;
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
