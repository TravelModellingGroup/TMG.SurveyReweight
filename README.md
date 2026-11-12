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
| Household | household ID, TAZ, initial expansion factor | One unique row per household |
| Person | person ID, household ID | Each household ID must exist in the household file |
| Trip | household ID, person ID | Each household and person ID must exist in the loaded records |

Set `HouseholdExpansionFactorColumn` to the zero-based column containing the
initial household expansion factors. These values must be finite, positive,
and within the configured minimum and maximum expansion factors. The selected
column is not treated as a constraint attribute. All other non-key columns
become numeric survey attributes. For example, a household file might contain
`household_id,taz,base_weight,workers`; configure `HouseholdExpansionFactorColumn`
as `2`, and `workers` remains available as an attribute.

### Constraint target files

For every `LoadConstraint`, configure:

1. `File`, containing one row for each target geography.
2. `MatchingAttributes`, using names from the corresponding household,
	 person, or trip CSV headers.
3. `GeographyColumn` and `TargetColumn`, as zero-based CSV column indexes.
4. `Tolerance`, which defaults to `0.01`.
5. `ConstraintWeight`, which defaults to `1.0`; larger values give this
   constraint more influence in the objective, while `0` excludes it from
   optimization.
6. An optional `CategoryMap` and constraint name.

The geography column contains sparse TAZ or mapped geography indexes, and the
target column contains the desired weighted total. A constraint with no
category map targets the zone system's TAZs. Put constraints in the matching
collection under `LoadConstraints`: household constraints use household
attributes, person constraints use person attributes, and trip constraints use
trip attributes.

### Outputs and tuning

`UpdatedExpansionFactorsFile` receives a CSV with one `UpdatedExpansionFactor`
value per household, in the same household order as the input records.
`ConstraintReportFile`, when connected, receives the constraint type and name,
the geography name (the configured `CategoryMap` module name, or `TAZ` when no
map is used), the sparse geography index, the target, final actual, absolute
difference, and ratio for every constraint geography plus a total row. The
total row uses `Global` in the `Index` column.

The calibration minimizes a weighted objective consisting of the
`ConstraintWeight`-weighted mean squared scaled constraint errors plus
`KLDivergencePenalty` times the generalized KL-divergence from the initial
expansion factors, normalized by their total.
For each constraint geography, the error is scaled by `max(abs(target), 1)`.
The KL term is
`sum(w * log(w / w0) - w + w0) / sum(w0)`, where `w0` is the input factor.
Increasing `KLDivergencePenalty` favors staying closer to the input factors;
decreasing it prioritizes fitting the constraints. The default is `1.0`.

`MaximumIterations` limits objective-minimization iterations. `Damping` scales
gradient steps relative to the configured expansion-factor range; a backtracking
line search reduces the step when needed to ensure the objective decreases.
