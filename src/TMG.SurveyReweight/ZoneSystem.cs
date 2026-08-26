namespace TMG.SurveyReweight;

[Module(
    Name = "LoadZoneSystem", 
    Description = "Loads the zone system for the reweighting process.",
    DocumentationLink = "https://tmg.utoronto.ca/doc/2.0/"
)]
public sealed class LoadZoneSystem : BaseFunction<ZoneSystem>
{
    
    [SubModule(Required = true, Name = "TAZ", Description = "The TAZ function to use for the reweighting process.", Index = 0)]
    public IFunction<Categories> TAZ = null!;

    [SubModule(Required = true, Name = "SuperZones", Description = "The Super Zones to use for the reweighting process.", Index = 1)]
    public IFunction<CategoryMap>[] SuperZones = null!;

    public override ZoneSystem Invoke()
    {
        return new ZoneSystem(TAZ.Invoke(), SuperZones.Select(f => f.Invoke()).ToArray());
    }
}

/// <summary>
/// Contains all of the spatial information for the reweighting process
/// </summary>
/// <param name="TAZ"></param>
/// <param name="SuperZones"></param>
public sealed record ZoneSystem (Categories TAZ, CategoryMap[] SuperZones);

