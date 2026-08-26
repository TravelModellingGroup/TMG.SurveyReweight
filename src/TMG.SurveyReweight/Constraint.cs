namespace TMG.SurveyReweight;

[Module(
    Name = "LoadConstraint",
    Description = "Loads a constraint for the reweighting process.",
    DocumentationLink = "https://tmg.utoronto.ca/doc/2.0/"
)]
public class LoadConstraint : BaseFunction<(ZoneSystem ZoneSystem, RecordAttribute[] MatchingAttributes), Constraint>
{
    public override Constraint Invoke((ZoneSystem ZoneSystem, RecordAttribute[] MatchingAttributes) context)
    {
        var zoneSystem = context.ZoneSystem;
        var matchingAttributes = context.MatchingAttributes;
        throw new NotImplementedException();
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
        var household = HouseholdConstraints.Select(f => f.Invoke((zoneSystem, surveyRecords.HouseholdAttributes))).ToArray();
        var person = PersonConstraints.Select(f => f.Invoke((zoneSystem, surveyRecords.PersonAttributes))).ToArray();
        var trip = TripConstraints.Select(f => f.Invoke((zoneSystem, surveyRecords.TripAttributes))).ToArray();
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
/// <param name="Name">The name of the constraint</param>
/// <param name="CategoryMap">The aggregation of geography to use, leave null for TAZ.</param>
/// <param name="MatchingAttributes">The survey record attributes to bind to.</param>
/// <param name="Targets">The target values for the constraint.</param>
public record Constraint(string Name, CategoryMap? CategoryMap, RecordAttribute[] MatchingAttributes, float[] Targets, float Tolerance)
{
    /// <summary>
    /// Update the constraint using the current expansion factors and provide a new set of expansion factors.
    /// </summary>
    /// <param name="caller">The module that is calling this method</param>
    /// <param name="data">The data of the current level</param>
    /// <param name="currentExpansionFactors">The current expansion factors (does not write to this)</param>
    /// <param name="scratch">Temporary scratch space for calculations</param>
    /// <param name="expIndexes">The indexes of the expansion factor for each record (also used for addressing the updatedExpansionFactors).</param>
    /// <param name="geographyIndex">The geography index for each record (used for matching to the constraint's Target geography)</param>
    /// <param name="updatedExpansionFactors">The updated expansion factors</param>
    /// <returns>True if the constraint is satisfied, otherwise false</returns>
    internal bool UpdateConstraint(IModule caller, float[] data, nuint numberOfAttributes, float[] currentExpansionFactors,
         float[] scratch, nuint[] expIndexes, nuint[] geographyIndex, float[] updatedExpansionFactors)
    {
        
        scratch.AsSpan().Clear();
        var numberOfRecords = (nuint)(data.Length / MatchingAttributes.Length);

        // Step 1 aggregate the current expansion factors to the
        for (nuint i = 0; i < numberOfRecords; i++)
        {
            // Match the records geography to the constraint's geography
            var recordExp = currentExpansionFactors[expIndexes[i]];

            // Go through all of the attributes and multiply them together to get the total expansion factor for this record.
            // TODO: In the future it might make sense to compute if a record matches or if it doesn't a single time to improve performance.
            for (nuint j = 0; j < (nuint)MatchingAttributes.Length; j++)
            {
                var attributeValue = data[i * numberOfAttributes + MatchingAttributes[j].Index];
                recordExp *= attributeValue;
            }

            scratch[geographyIndex[i]] += recordExp;
        }

        // Step 2 compare the results to the targets and compute how we need to reweight the expansion factors to meet the targets.
        var withinTolerance = true;
        for (nuint i = 0; i < (nuint)Targets.Length; i++)
        {
            if (scratch[i] <= 0)
            {
                // If the current expansion factor is zero, we cannot reweight it to meet the target.
                // This means that the constraint is not satisfied.
                throw new XTMFRuntimeException(caller, $"Constraint {Name} is not satisfied because the current expansion factor for geography index {i} is zero.");
            }
            var ratio = Targets[i] / scratch[i];
            updatedExpansionFactors[i] = ratio;
            // Check to see if the constraint is satisfied within the tolerance
            if (Math.Abs(ratio - 1.0f) > Tolerance)
            {
                // If the ratio is not within the tolerance, the constraint is not satisfied.
                withinTolerance = false;
            }
        }

        // Step 3 write to updatedExpansionFactors the new expansion factors for each record.
        for (nuint i = 0; i < numberOfRecords; i++)
        {
            updatedExpansionFactors[expIndexes[i]] = currentExpansionFactors[expIndexes[i]] * updatedExpansionFactors[geographyIndex[i]];
        }


        // Implement the logic to update the expansion factors based on the constraint
        // Return true if the constraint is satisfied, otherwise false
        return withinTolerance;
    }
}


public sealed class Constraints
{
    public required Constraint[] HouseholdConstraints { get; init; } = [];

    public required Constraint[] PersonConstraints { get; init; } = [];

    public required Constraint[] TripConstraints { get; init; } = [];
}
