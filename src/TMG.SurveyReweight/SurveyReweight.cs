using XTMF2.RuntimeModules;

namespace TMG.SurveyReweight;

public class SurveyReweight : BaseAction
{
    [SubModule(Required = true, Name = "ZoneSystem", Description = "The zone system to use for the reweighting process.", Index = 0)]
    public IFunction<ZoneSystem> ZoneSystem = null!;

    [SubModule(Required = true, Name = "SurveyRecords", Description = "The survey records to use for the reweighting process.", Index = 1)]
    public IFunction<SurveyRecords> SurveyRecords = null!;

    [SubModule(Required = true, Name = "Constraints", Description = "The constraints to use for the reweighting process.", Index = 2)]
    public IFunction<(ZoneSystem, SurveyRecords), Constraints> Constraints = null!;

    [SubModule(Required = true, Name = "MaximumIterations", Description = "The maximum number of iterations for the reweighting process.", Index = 3)]
    public IFunction<int> MaximumIterations = null!;

    [SubModule(Required = false, Name = "Log", Description = "The log for the reweighting process.", Index = 4)]
    public IFunction<Log>? Log;

    public override void Invoke()
    {
        var log = Log?.Invoke();
        // Step 1 read in zone system and Super Zones
        log?.Invoke($"Reading in zone system and super zones...");
        var zoneSystem = ZoneSystem.Invoke();
        // Step 2 read in survey records (Household, Person, and Trip)
        log?.Invoke($"Reading in survey records...");
        var surveyRecords = SurveyRecords.Invoke();
        // Step 3 read in constraints (Household, Person, and Trip)
        log?.Invoke($"Reading in constraints...");
        var constraints = Constraints.Invoke((zoneSystem, surveyRecords));
        var maxIterations = MaximumIterations.Invoke();
        

        // Step 4 initialize the expansion factors and temporary compute space for the reweighting process
        log?.Invoke($"Initializing expansion factors and temporary compute space...");
        float[] currentExpansionFactors = new float[surveyRecords.NumberOfHouseholds];
        nuint[] householdExpansionIndex = new nuint[surveyRecords.NumberOfHouseholds];
        for (nuint i = 0; i < surveyRecords.NumberOfHouseholds; i++)
        {
            householdExpansionIndex[i] = i;
        }

        // Scratch is used for aggregating the current total of expansion factors for the currently weighted expansions
        float[][] tempConstraintHouseholdScratch = new float[constraints.HouseholdConstraints.Length][];
        float[][] tempConstraintPersonScratch = new float[constraints.PersonConstraints.Length][];
        float[][] tempConstraintTripScratch = new float[constraints.TripConstraints.Length][];

        // Temp is used for storing what each constraint wants to modify the household expansion factor to be.
        float[][] tempExpansionHousehold = new float[constraints.HouseholdConstraints.Length][];
        float[][] tempExpansionPerson = new float[constraints.PersonConstraints.Length][];
        float[][] tempExpansionTrip = new float[constraints.TripConstraints.Length][];

        nuint[][] householdGeographyIndex = new nuint[constraints.HouseholdConstraints.Length][];
        nuint[][] personGeographyIndex = new nuint[constraints.PersonConstraints.Length][];
        nuint[][] tripGeographyIndex = new nuint[constraints.TripConstraints.Length][];

        currentExpansionFactors.AsSpan().Fill(1.0f);

        // Initialize the data for each constraint, household, person, and trip level.
        InitializeConstraintData(
            constraints.HouseholdConstraints,
            surveyRecords.NumberOfHouseholds,
            i => surveyRecords.HouseholdTAZ[i],
            out tempConstraintHouseholdScratch,
            out tempExpansionHousehold,
            out householdGeographyIndex);

        InitializeConstraintData(
            constraints.PersonConstraints,
            surveyRecords.NumberOfPersons,
            i => surveyRecords.HouseholdTAZ[surveyRecords.PersonHouseholdIndex[i]],
            out tempConstraintPersonScratch,
            out tempExpansionPerson,
            out personGeographyIndex);

        InitializeConstraintData(
            constraints.TripConstraints,
            surveyRecords.NumberOfTrips,
            i => surveyRecords.HouseholdTAZ[surveyRecords.TripHouseholdIndex[i]],
            out tempConstraintTripScratch,
            out tempExpansionTrip,
            out tripGeographyIndex);
        

        // Step 5 run the reweighting process
        int iteration = 0;
        for (; iteration < maxIterations; iteration++)
        {
            log?.Invoke($"Running iteration {iteration + 1} of {maxIterations}...");
            bool allConstraintsSatisfied = true;
            
            // TODO: Switch this to all run in parallel later.
            for (nuint i = 0; i < (nuint)constraints.HouseholdConstraints.Length; i++)
            {
                var constraint = constraints.HouseholdConstraints[i];
                var scratch = tempConstraintHouseholdScratch[i];
                var expansion = tempExpansionHousehold[i];
                var geography = householdGeographyIndex[i];

                if(!constraint.UpdateConstraint(this, surveyRecords.HouseholdData, (nuint)surveyRecords.HouseholdAttributes.Length, currentExpansionFactors, scratch, householdExpansionIndex, geography, expansion))
                {
                    allConstraintsSatisfied = false;
                }
            }

            for (nuint i = 0; i < (nuint)constraints.PersonConstraints.Length; i++)
            {
                var constraint = constraints.PersonConstraints[i];
                var scratch = tempConstraintPersonScratch[i];
                var expansion = tempExpansionPerson[i];
                var geography = personGeographyIndex[i];

                if(!constraint.UpdateConstraint(this, surveyRecords.PersonData, (nuint)surveyRecords.PersonAttributes.Length, currentExpansionFactors, scratch, surveyRecords.PersonHouseholdIndex, geography, expansion))
                {
                    allConstraintsSatisfied = false;
                }
            }

            for (nuint i = 0; i < (nuint)constraints.TripConstraints.Length; i++)
            {
                var constraint = constraints.TripConstraints[i];
                var scratch = tempConstraintTripScratch[i];
                var expansion = tempExpansionTrip[i];
                var geography = tripGeographyIndex[i];

                if(!constraint.UpdateConstraint(this, surveyRecords.TripData, (nuint)surveyRecords.TripAttributes.Length, currentExpansionFactors, scratch, surveyRecords.TripHouseholdIndex, geography, expansion))
                {
                    allConstraintsSatisfied = false;
                }
            }
            
            // Check if all of the contraints are satisfied
            if (allConstraintsSatisfied)
            {
                break;
            }
        }
        log?.Invoke($"Reweighting process completed in {iteration + 1} iterations of {maxIterations}.");
    }

    private void InitializeConstraintData(
        Constraint[] constraints,
        nuint numberOfRecords,
        Func<nuint, CategoryIndex> getRecordTAZ,
        out float[][] constraintScratch,
        out float[][] expansion,
        out nuint[][] geographyIndex)
    {
        constraintScratch = new float[constraints.Length][];
        expansion = new float[constraints.Length][];
        geographyIndex = new nuint[constraints.Length][];

        for (int i = 0; i < constraints.Length; i++)
        {
            var constraint = constraints[i];
            constraintScratch[i] = new float[constraint.Targets.Length];
            expansion[i] = new float[numberOfRecords];
            geographyIndex[i] = new nuint[numberOfRecords];

            var map = constraint.CategoryMap;
            if (map is null)
            {
                for (nuint j = 0; j < numberOfRecords; j++)
                {
                    geographyIndex[i][j] = (nuint)getRecordTAZ(j);
                }
                continue;
            }

            var lookup = map.CreateIndex();
            for (nuint j = 0; j < numberOfRecords; j++)
            {
                var recordTAZ = getRecordTAZ(j);
                if (!lookup.TryGetValue(recordTAZ, out var mappedGeography))
                {
                    throw new XTMFRuntimeException(this, $"Record {j} TAZ {recordTAZ} could not be mapped to the constraint {constraint.Name}'s geography.");
                }
                geographyIndex[i][j] = (nuint)mappedGeography;
            }
        }
    }
}
