# KustoDependencyAnalyser
## Background
This tool is used to analyze dependencies of given nuget packages from Kusto database "azscperf.westus.kusto.windows.net" and generate summary CSV files.

## Requirements
- The program requires access to a Kusto cluster and database that contains the appropriate tables for analysis.
- The program requires a `Packages.props` file to define the version for each package.
- The program requires a list of root packages to analyze.

## Usage
1. Replace the `Packages.props` with latest version
2. Update root packages in `roots.txt`
3. Run the application to generate the following CSV files:
- `Packages.csv`: This file shows the relationships between all root packages and their dependencies.
- `DllInfo.csv`: This file shows all DLL filenames and their corresponding packages and root packages.
- `VersionConflicts.csv`: This file shows all detected version conflicts between required dependency range in Kusto and actual dependency version in Packages.props.

## Configuration
- `Cluster`: The Kusto cluster that the data will be queried from.
- `Database`: The name of the Kusto database.
- `PackageAssemblyTable`: The name of the Kusto table that contains all assembly information.
- `PackageDependencyTable`: The name of the Kusto table that contains all dependency information.
- `PackagesCSVFile`: The name of the CSV file to store the package and dependency information.
- `DllInfoCSVFile`: The name of the CSV file to store the DLL and package information.
- `DefaultRootFile`: The name of the file that contains the list of all root packages to analyze.
- `DefaultPackagesPropsFile`: The name of the `Packages.props` file.
- `VersionConflictsFile`: The name of the CSV file to store the version conflict information.

## How Does It Work?
1. The program loads a list of root packages to analyze.
2. For each root package, the program uses DFS algorithm to querie all its dependencies from the Kusto table and stores the results in memory.
3. After all dependencies are loaded, the program generates three CSV files to summarize the dependencies and version conflicts.