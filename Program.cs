using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using System.Text.RegularExpressions;

namespace KustoDependencyAnalyser

{
    internal sealed record Package(string Name, string Version) : IComparable<Package>
    {
        public int CompareTo(Package? package)
        {
            if (package == null)
            {
                return 1;
            }
            return Name.CompareTo(package.Name);
        }

        public bool Equals(Package? package)
        {
            if (package == null)
            {
                return false;
            }
            return Name == package.Name;
        }
    }

    // The program should execute in an interactive context (so that on first run the user
    // will get asked to sign in to Azure AD to access the Kusto service).
    class Program
    {
        private const string Cluster = "azscperf.westus.kusto.windows.net";
        private const string Database = "dependency";
        private const string PackageAssemblyTable = "PackageAssembly";
        private const string PackageDependencyTable = "PackageDependency";
        private const string PackagesCSVFile = "Packages.csv";
        private const string DllInfoCSVFile = "DllInfo.csv";
        private const string DefaultRootFile = "roots.txt";
        private const string DefaultPackagesPropsFile = "Packages.props";
        private const string VersionConflictsFile = "VersionConflicts.csv";

        private static readonly KustoConnectionStringBuilder Kscb = new KustoConnectionStringBuilder(Cluster, Database)
            .WithAadUserPromptAuthentication();
        private static readonly SortedSet<Package> Packages = new();
        private static readonly SortedSet<string> Conflicts = new();

        private static void InitializePackages()
        {
            using StreamReader sr = new(DefaultRootFile);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                Packages.Add(new Package(line, GetVersionInPackagesProps(line)));
            }
        }

        private static string GetVersionInPackagesProps(string packageName)
        {
            using StreamReader sr = new(DefaultPackagesPropsFile);
            string? line;
            var pattern = "\\s*<PackageVersion (Include|Update)=\".+\" Version=\".+\"\\s*/>\\s*";
            var version = "";
            while ((line = sr.ReadLine()) != null)
            {
                Match match = Regex.Match(line, pattern);
                if (match.Success)
                {
                    var packageNameStart = line.NthIndexOf('\"', 1) + 1; // Nthndexof is 1-based
                    var packageNameEnd = line.NthIndexOf('\"', 2);
                    if (line[packageNameStart..packageNameEnd] == packageName)
                    {
                        var versionStart = line.NthIndexOf('\"', 3) + 1;
                        var versionEnd = line.NthIndexOf('\"', 4);
                        version = line[versionStart..versionEnd];
                    }
                }
            }
            return version;
        }

        private static List<string> GetDllsOfPackage(Package package)
        {
            var dlls = new SortedSet<string>();
            var packageName = package.Name;
            var version = GetVersionInPackagesProps(packageName);
            var query = $"{PackageAssemblyTable} | where Name == \"{packageName}\" and Version == \"{version}\"";
            using var queryProvider = KustoClientFactory.CreateCslQueryProvider(Kscb);
            var clientRequestProperties = new ClientRequestProperties() { ClientRequestId = Guid.NewGuid().ToString() };
            using var reader = queryProvider.ExecuteQuery(query, clientRequestProperties);

            /*
             Schema of package assembly table:
            0   Name: string
            1   Version: string
            2   AssemblyName: string
            3   AssemblyVersion: string
            4   LibraryDirectoryPath: string
            5   AssemblySourceType: string
            6   AssemblySource: string
             */
            while (reader.Read())
            {
                dlls.Add($"{reader.GetString(0)},{reader.GetString(1)},{reader.GetString(2)},{reader.GetString(3)},{reader.GetString(4)}");
            }
            return dlls.ToList();
        }

        /*
         Known format:
        Attention: There is always a space after comma
        (, )
        (, version)
        (version,version)
        [version, ) 
        [version, version)
        [version, version]
         */
        private static void CheckVersionConflicts(string packageName, string packageVersion, string dependencyName, string dependencyVersion, string versionRange, string targetFramework)
        {
            bool lbIncluded;
            bool ubIncluded;
            if (versionRange.StartsWith('('))
            {
                if (versionRange.EndsWith(')'))
                {
                    lbIncluded = false;
                    ubIncluded = false;
                }
                else
                {
                    throw new Exception("Unknown version range format");
                }
            }
            else if (versionRange.StartsWith('['))
            {
                if (versionRange.EndsWith(')'))
                {
                    lbIncluded = true;
                    ubIncluded = false;
                }
                else if (versionRange.EndsWith(']'))
                {
                    lbIncluded = true;
                    ubIncluded = true;
                }
                else
                {
                    throw new Exception("Unknown version range format");
                }
            }
            else
            {
                throw new Exception("Unknown version range format");
            }

            var pattern = @"([\d.]+)(.*)"; // split numeric part and suffix part
            var matchLB = Regex.Match(versionRange.Substring(1, versionRange.IndexOf(',') - 1), pattern);
            var lowerBound = matchLB.Groups[1].Value;
            var matchUB = Regex.Match(versionRange.Substring(versionRange.IndexOf(',') + 2, versionRange.Length - versionRange.IndexOf(',') - 3), pattern);
            var upperBound = matchUB.Groups[1].Value;
            var matchRequired = Regex.Match(dependencyVersion, pattern);
            var versionNumericPart = matchRequired.Groups[1].Value;
            if (!IsVersionValid(lowerBound, upperBound, lbIncluded, ubIncluded, versionNumericPart))
            {
                Conflicts.Add($"{targetFramework},{packageName},{packageVersion},{dependencyName},\"{versionRange}\",{dependencyVersion}");
            }
        }

        private static bool IsVersionValid(string lb, string ub, bool lbIncluded, bool ubIncluded, string version)
        {
            var required = Version.Parse(version);
            var lbExist = lb != "";
            var ubExist = ub != "";
            var lowBound = lbExist ? Version.Parse(lb) : null;
            var upperBound = ubExist ? Version.Parse(ub) : null;
            bool lbValid = !lbExist || (lbIncluded ? required >= lowBound : required > lowBound);
            bool ubValid = !ubExist || (ubIncluded ? required <= upperBound : required < upperBound);
            return lbValid && ubValid;
        }

        /*
         Query the package dependency table to get the dependencies of a package.
         */
        private static List<Package> QueryDependencies(Package package)
        {
            var dependencies = new List<Package>();
            var packageName = package.Name;
            var packageVersion = GetVersionInPackagesProps(packageName);

            //Stopwatch sw = new();
            //sw.Start();
            var query = $"{PackageDependencyTable} | where Name == \"{packageName}\" and Version == \"{packageVersion}\"";
            using var queryProvider = KustoClientFactory.CreateCslQueryProvider(Kscb);
            var clientRequestProperties = new ClientRequestProperties() { ClientRequestId = Guid.NewGuid().ToString() };
            using var reader = queryProvider.ExecuteQuery(query, clientRequestProperties);
            //sw.Stop();
            //Console.WriteLine($"Time of querying dependencies elapsed: {sw.Elapsed}");

            /*
             Schema of package dependency table:
            0   Name: string
            1   Version: string
            2   TargetFramework: string
            3   DependencyName: string
            4   DependencyVersionRange: string
             */
            while (reader.Read())
            {
                var targetFramework = reader.GetString(2);
                var dependencyName = reader.GetString(3);
                var dependencyVersionRange = reader.GetString(4);
                var dependcyVersion = GetVersionInPackagesProps(dependencyName);
                if (dependcyVersion != "")
                {
                    dependencies.Add(new Package(dependencyName, dependcyVersion));
                    CheckVersionConflicts(packageName, packageVersion, dependencyName, dependcyVersion, dependencyVersionRange, targetFramework);
                }
            }
            return dependencies;
        }

        private static void DfsGetDependencies()
        {
            var stack = new Stack<Package>(Packages.ToList());
            while (stack.Count > 0)
            {
                var package = stack.Pop();
                Console.WriteLine($"Processing {package.Name}");
                var dependencies = QueryDependencies(package);
                foreach (var dependency in dependencies)
                {
                    if (!Packages.Contains(dependency))
                    {
                        Packages.Add(dependency);
                        stack.Push(dependency);
                    }
                }
            }
        }

        private static void GeneratePackagesCSV()
        {
            using StreamWriter sw = new(PackagesCSVFile);
            sw.WriteLine("packageName,packageVersion");
            foreach (var package in Packages)
            {
                sw.WriteLine($"{package.Name},{package.Version}");
            }
        }

        private static void GenerateDllInfoCSV()
        {
            using StreamWriter sw = new(DllInfoCSVFile);
            sw.WriteLine("packageName,packageVersion,dllName,dllVersion,libraryDirectoryPath");
            foreach (var package in Packages)
            {
                var dlls = GetDllsOfPackage(package);
                foreach (var dll in dlls)
                {
                    sw.WriteLine(dll);
                }
            }
        }

        private static void GenerateConflictsCSV()
        {
            using StreamWriter sw = new(VersionConflictsFile);
            sw.WriteLine("targetFramework,packageName,packageVersion,dependencyName,versionRange,dependencyVersion");
            foreach (var conflict in Conflicts)
            {
                sw.WriteLine(conflict);
            }
        }

        static void Main()
        {
            InitializePackages();
            DfsGetDependencies();
            GeneratePackagesCSV();
            GenerateDllInfoCSV();
            GenerateConflictsCSV();
        }
    }
}