namespace TMG.SurveyReweight;

/// <summary>
/// Contains all of the information revolving around the survey records for the reweighting process.
/// </summary>
public sealed class SurveyRecords
{
    public required RecordAttribute[] HouseholdAttributes { get; init; } = [];

    public CategoryIndex[] HouseholdTAZ { get; init; } = [];
    
    /// <summary>
    /// HouseholdAttribues x NumberOfHouseholds
    /// </summary>
    public float[] HouseholdData { get; init; } = [];

    public required RecordAttribute[] PersonAttributes { get; init; } = [];

    /// <summary>
    /// PersonAttributes x NumberOfPersons
    /// </summary>
    public float[] PersonData { get; init; } = [];

    public nuint[] PersonHouseholdIndex { get; init; } = [];

    public required RecordAttribute[] TripAttributes { get; init; } = [];

    /// <summary>
    /// TripAttributes x NumberOfTrips
    /// </summary>
    public float[] TripData { get; init; } = [];

    /// <summary>
    /// The index of the household that makes this trip
    /// </summary>
    public nuint[] TripHouseholdIndex { get; init; } = [];

    /// <summary>
    /// The index of the person that makes this trip
    /// </summary>
    public nuint[] TripPersonIndex { get; init; } = [];

    /// <summary>
    /// The number of households in the survey records
    /// </summary>
    public nuint NumberOfHouseholds { get; internal set; }

    /// <summary>
    /// The number of persons in the survey records
    /// </summary>
    public nuint NumberOfPersons { get; internal set; }

    /// <summary>
    /// The number of trips in the survey records
    /// </summary>
    public nuint NumberOfTrips { get; internal set; }
}
