namespace TMG.SurveyReweight;

using TMG.Utilities;

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

[Module(
    Name = "LoadSurveyRecords",
    Description = "Loads the survey records from CSV files.",
    DocumentationLink = "https://tmg.utoronto.ca/doc/2.0/"
)]
public sealed class LoadSurveyRecords : BaseFunction<ZoneSystem, SurveyRecords>
{
    [SubModule(Required = true, Name = "HouseholdFile", Description = "The CSV file containing household records.", Index = 0)]
    public IFunction<ReadStream> HouseholdFile = null!;

    [SubModule(Required = true, Name = "PersonFile", Description = "The CSV file containing person records.", Index = 1)]
    public IFunction<ReadStream> PersonFile = null!;

    [SubModule(Required = true, Name = "TripFile", Description = "The CSV file containing trip records.", Index = 2)]
    public IFunction<ReadStream> TripFile = null!;

    [Parameter(DefaultValue = "0", Name = "HouseholdIDColumn", Index = 3, Description = "The zero-based household CSV column containing the unique household identifier.")]
    public IFunction<int> HouseholdIDColumn = null!;

    [Parameter(DefaultValue = "1", Name = "HouseholdTAZColumn", Index = 4, Description = "The zero-based household CSV column containing the TAZ index.")]
    public IFunction<int> HouseholdTAZColumn = null!;

    [Parameter(DefaultValue = "0", Name = "PersonIDColumn", Index = 5, Description = "The zero-based person CSV column containing the unique person identifier.")]
    public IFunction<int> PersonIDColumn = null!;

    [Parameter(DefaultValue = "1", Name = "PersonHouseholdColumn", Index = 6, Description = "The zero-based person CSV column containing the household identifier.")]
    public IFunction<int> PersonHouseholdColumn = null!;

    [Parameter(DefaultValue = "0", Name = "TripHouseholdColumn", Index = 7, Description = "The zero-based trip CSV column containing the household identifier.")]
    public IFunction<int> TripHouseholdColumn = null!;

    [Parameter(DefaultValue = "1", Name = "TripPersonColumn", Index = 8, Description = "The zero-based trip CSV column containing the person identifier.")]
    public IFunction<int> TripPersonColumn = null!;

    public override SurveyRecords Invoke(ZoneSystem context)
    {
        var householdIDColumn = HouseholdIDColumn.Invoke();
        var householdTAZColumn = HouseholdTAZColumn.Invoke();
        var personIDColumn = PersonIDColumn.Invoke();
        var personHouseholdColumn = PersonHouseholdColumn.Invoke();
        var tripHouseholdColumn = TripHouseholdColumn.Invoke();
        var tripPersonColumn = TripPersonColumn.Invoke();

        LoadTable(
            HouseholdFile.Invoke(),
            "household",
            [householdIDColumn, householdTAZColumn],
            out var householdAttributes,
            out var householdData,
            out var householdKeys);
        LoadTable(
            PersonFile.Invoke(),
            "person",
            [personIDColumn, personHouseholdColumn],
            out var personAttributes,
            out var personData,
            out var personKeys);
        LoadTable(
            TripFile.Invoke(),
            "trip",
            [tripHouseholdColumn, tripPersonColumn],
            out var tripAttributes,
            out var tripData,
            out var tripKeys);

        var householdCount = (nuint)householdKeys.Length;
        var householdIDs = CreateUniqueIndex(householdKeys, 0, "household");
        var personIDs = CreateUniqueIndex(personKeys, 0, "person");
        var personHouseholdIndex = new nuint[personKeys.Length];
        for (int i = 0; i < personKeys.Length; i++)
        {
            personHouseholdIndex[i] = FindIndex(personKeys[i][1], householdIDs, "person household", i);
        }

        var tripHouseholdIndex = new nuint[tripKeys.Length];
        var tripPersonIndex = new nuint[tripKeys.Length];
        for (int i = 0; i < tripKeys.Length; i++)
        {
            tripHouseholdIndex[i] = FindIndex(tripKeys[i][0], householdIDs, "trip household", i);
            tripPersonIndex[i] = FindIndex(tripKeys[i][1], personIDs, "trip person", i);
        }

        var householdTAZ = new CategoryIndex[householdKeys.Length];
        var taz = context.TAZ;
        for (int i = 0; i < householdKeys.Length; i++)
        {
            householdTAZ[i] = taz.GetFlatIndex(householdKeys[i][1]);
        }

        return new SurveyRecords
        {
            HouseholdAttributes = householdAttributes,
            HouseholdTAZ = householdTAZ,
            HouseholdData = householdData,
            PersonAttributes = personAttributes,
            PersonData = personData,
            PersonHouseholdIndex = personHouseholdIndex,
            TripAttributes = tripAttributes,
            TripData = tripData,
            TripHouseholdIndex = tripHouseholdIndex,
            TripPersonIndex = tripPersonIndex,
            NumberOfHouseholds = householdCount,
            NumberOfPersons = (nuint)personKeys.Length,
            NumberOfTrips = (nuint)tripKeys.Length
        };
    }

    private void LoadTable(
        ReadStream stream,
        string level,
        int[] keyColumns,
        out RecordAttribute[] attributes,
        out float[] data,
        out int[][] keys)
    {
        if (keyColumns.Any(column => column < 0) || keyColumns.Distinct().Count() != keyColumns.Length)
        {
            throw new XTMFRuntimeException(this, $"The {level} key columns must be distinct and greater than or equal to zero.");
        }

        var attributeList = new List<RecordAttribute>();
        var dataList = new List<float>();
        var keyList = new List<int[]>();

        using var reader = new CsvReader(stream, true);
        var headers = reader.Headers;
        var keySet = keyColumns.ToHashSet();
        for (int i = 0; i < headers.Length; i++)
        {
            if (!keySet.Contains(i))
            {
                attributeList.Add(new RecordAttribute { Name = headers[i], Index = (nuint)attributeList.Count });
            }
        }

        var maxKeyColumn = keyColumns.Max();
        try
        {
            while (reader.LoadLine(out var columns))
            {
                if (columns <= maxKeyColumn || columns < headers.Length)
                {
                    throw new XTMFRuntimeException(this, $"The {level} file {reader.FileName} has too few columns on line {reader.LineNumber}.");
                }

                var rowKeys = new int[keyColumns.Length];
                for (int i = 0; i < keyColumns.Length; i++)
                {
                    reader.Get(out rowKeys[i], keyColumns[i]);
                }
                keyList.Add(rowKeys);

                for (int i = 0; i < headers.Length; i++)
                {
                    if (!keySet.Contains(i))
                    {
                        reader.Get(out float value, i);
                        dataList.Add(value);
                    }
                }
            }
        }
        catch (XTMFRuntimeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new XTMFRuntimeException(this, $"Unable to read the {level} file {reader.FileName} on line {reader.LineNumber}: {exception.Message}");
        }

        attributes = attributeList.ToArray();
        data = dataList.ToArray();
        keys = keyList.ToArray();
    }

    private Dictionary<int, nuint> CreateUniqueIndex(int[][] keys, int keyPosition, string level)
    {
        var index = new Dictionary<int, nuint>(keys.Length);
        for (int row = 0; row < keys.Length; row++)
        {
            var id = keys[row][keyPosition];
            if (!index.TryAdd(id, (nuint)row))
            {
                throw new XTMFRuntimeException(this, $"The {level} identifier {id} is specified more than once on row {row}.");
            }
        }
        return index;
    }

    private nuint FindIndex(int id, Dictionary<int, nuint> index, string description, int row)
    {
        if (!index.TryGetValue(id, out var recordIndex))
        {
            throw new XTMFRuntimeException(this, $"The {description} identifier {id} on row {row} does not match a loaded record.");
        }
        return recordIndex;
    }
}