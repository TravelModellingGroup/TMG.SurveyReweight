# TMG.SurveyReweight

`TMG.SurveyReweight` provides XTMF2 modules for calibrating household survey
records against household, person, and trip-level population constraints. The
reweighting process starts every household with an expansion factor of `1.0`
and iteratively applies the constraint corrections. The resulting household
expansion factors are written to a CSV file, with an optional constraint report
for checking the final results.

## Requirements

- .NET SDK 10.0 or later
- A checkout of the XTMF2 repository and TMG-Framework referenced by the
	project file
- An XTMF2 development installation when running the modules in a model
	system

The project targets `net10.0`. Its build output is configured for the local
XTMF2 development module directory at `../XTMF2-Dev/Modules` relative to the
repository's parent XTMF2 directory.

## Compile

From the repository root, build the project in Release configuration:

```sh
dotnet build -c Release
```

To build the solution instead:

```sh
dotnet build -c Release
```

The project references `XTMF2.Interfaces`, `XTMF2`, and `TMG-Framework` as
sibling projects. If those projects are not at the paths expected by
`TMG.SurveyReweight.csproj`, update the project references or check out the
repositories in the expected layout before compiling.

## Constructing an XTMF2 Model System

The module assembly discovers the following XTMF2 modules:

- `SurveyReweight` (`BaseAction`): runs the calibration and writes the updated
	expansion factors.
- `LoadZoneSystem`: loads TAZs and optional super-zone maps.
- `LoadSurveyRecords`: loads household, person, and trip CSV files.
- `LoadConstraints`: groups constraints by record level.
- `LoadConstraint`: loads one target file and binds it to survey attributes.

Create the model system with this structure in the XTMF2 model editor:

```text
SurveyReweight
|- ZoneSystem: LoadZoneSystem
|  |- TAZ
|  `- SuperZones (optional, one CategoryMap per map)
|- SurveyRecords: LoadSurveyRecords
|  |- HouseholdFile
|  |- PersonFile
|  `- TripFile
|- Constraints: LoadConstraints
|  |- HouseholdConstraints (zero or more LoadConstraint modules)
|  |- PersonConstraints (zero or more LoadConstraint modules)
|  `- TripConstraints (zero or more LoadConstraint modules)
|- MaximumIterations: 100
|- Damping: 1.0
|- UpdatedExpansionFactorsFile
|- Log (optional)
`- ConstraintReportFile (optional)
```

The `ZoneSystem` TAZ categories must use the same sparse category identifiers
as the household CSV's TAZ column. A constraint may use a `CategoryMap` to
aggregate those TAZs into a different geography. The map's base categories
must be exactly the zone system TAZs, and its destination categories are the
constraint geographies.

### Survey CSV files

`LoadSurveyRecords` expects a header row and numeric values in every non-key
column. Configure the zero-based key columns on the loader:

| File | Required keys | Meaning |
| --- | --- | --- |
| Household | household ID, TAZ | One unique row per household |
| Person | person ID, household ID | Each household ID must exist in the household file |
| Trip | household ID, person ID | Each household and person ID must exist in the loaded records |

All columns other than the configured keys become numeric survey attributes.
For example, a household file might contain `household_id,taz,workers` and a
person file might contain `person_id,household_id,age_group`. The attribute
names are taken directly from the headers and are used when configuring
constraints.

### Constraint target files

For every `LoadConstraint`, configure:

1. `File`, containing one row for each target geography.
2. `MatchingAttributes`, using names from the corresponding household,
	 person, or trip CSV headers.
3. `GeographyColumn` and `TargetColumn`, as zero-based CSV column indexes.
4. `Tolerance`, which defaults to `0.01`.
5. An optional `CategoryMap` and constraint name.

The geography column contains sparse TAZ or mapped geography indexes, and the
target column contains the desired weighted total. A constraint with no
category map targets the zone system's TAZs. Put constraints in the matching
collection under `LoadConstraints`: household constraints use household
attributes, person constraints use person attributes, and trip constraints use
trip attributes.

### Outputs and tuning

`UpdatedExpansionFactorsFile` receives a CSV with one `UpdatedExpansionFactor`
value per household, in the same household order as the input records.
`ConstraintReportFile`, when connected, receives the target, final actual,
absolute difference, and ratio for every constraint geography plus a total
row.

`MaximumIterations` limits the calibration loop. `Damping` controls how much
of each combined correction is applied per iteration: `1.0` applies the full
correction, while smaller positive values make the updates more gradual.
