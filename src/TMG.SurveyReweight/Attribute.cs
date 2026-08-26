namespace TMG.SurveyReweight;

/// <summary>
/// We are simplifying things by making the data records simplified.
/// Only numbers are allowed, they will be floating point 1 for yes, 0 for no, and if we are dealing with things like transit boardings 
/// it can be greater than 1 so all of the processsing is just the same.
/// </summary>
public class RecordAttribute
{
    public required string Name { get; init; }

    /// <summary>
    /// The index into this level's attributes
    /// </summary>
    public nuint Index { get; internal set; }
}

