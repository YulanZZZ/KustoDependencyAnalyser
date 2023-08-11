using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Linq;
using Kusto.Data.Net.Client;
using System.Security.Cryptography;

namespace KustoDependencyAnalyser

{
    // This sample illustrates how to query Kusto using the Kusto.Data .NET library.
    //
    // For the purpose of demonstration, the query being sent retrieves multiple result sets.
    //
    // The program should execute in an interactive context (so that on first run the user
    // will get asked to sign in to Azure AD to access the Kusto service).
    class Program
    {
        private const string Cluster = "azscperf.westus.kusto.windows.net";
        private const string Database = "dependency";
        private const string PackageAssemblyTable = "PackageAssembly";
        private const string PackageDependencyTable = "PackageDependency";
        private const string PackageDependencyCSVFile = "PackageDependency.csv";
        private const string DllInfoCSVFile = "DllInfo.csv";
        private const string DefaultRootFile = "roots.txt";
        private const string DefaultPackagesPropsFile = "Packages.props";
        private const string PackageVersionMissingFile = "PackageVersionMissing.txt";
        private const string VersionConflictsFile = "VersionConflicts.txt";

        private static readonly KustoConnectionStringBuilder Kscb = new KustoConnectionStringBuilder(Cluster, Database)
            .WithAadUserPromptAuthentication();
        private static readonly HashSet<string> Packages = new();

        private static void InitializePackages()
        {
            using StreamReader sr = new(DefaultRootFile);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                Packages.Add(line);
            }
        }

        private static string GetVersionInPackagesProps(string packageName)
        {
            using StreamReader sr = new(DefaultPackagesPropsFile);
            string? line;
            var pattern = "\\s*<PackageVersion Include=\".+\" Version=\".+\"\\s*/>\\s*";

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
                        return line[versionStart..versionEnd];
                    }
                }
            }
            return "";
        }

        private static List<string> GetDllsOfPackage(string packageName)
        {
            var dlls = new SortedSet<string>();
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

        private static void HandleVersionIssues(string packageName, string packageVersion, string dependencyName, string dependencyVersion, string dependencyVersionRange)
        {
            if (dependencyVersion == "")
            {
                HandleVersionMissing(dependencyName, dependencyVersion);
            }
            else
            {
                CheckVersionConflicts(packageName, packageVersion, dependencyName, dependencyVersion, dependencyVersionRange);
            }

        }

        private static void HandleVersionMissing(string packageName, string version)
        {
            if (version == "")
            {
                using StreamWriter sw = new(PackageVersionMissingFile, true);
                sw.WriteLine($"{packageName} {version}");
            }
        }

        private static void HandleDependency(string packageName, string packageVersion, string targetFramework, string dependencyName, string dependencyVersion)
        {
            using StreamWriter sw = new(PackageDependencyCSVFile, true);
            sw.WriteLine($"{packageName},{packageVersion},{targetFramework},{dependencyName},{dependencyVersion}");
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
        private static void CheckVersionConflicts(string packageName, string packageVersion, string dependencyName, string dependencyVersion, string versionRange)
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
                using StreamWriter sw = new(VersionConflictsFile, true);
                sw.WriteLine($"Version conflict: {packageName} {dependencyVersion} depends on {dependencyName} {versionRange}, but version in Packages.props is {dependencyVersion}");
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
        private static List<string> QueryDependencies(string packageName)
        {
            var dependencies = new List<string>();
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
                dependencies.Add(dependencyName);
                var dependcyVersion = GetVersionInPackagesProps(dependencyName);
                HandleVersionIssues(packageName, packageVersion, dependencyName, dependcyVersion, dependencyVersionRange);
                HandleDependency(packageName, packageVersion, targetFramework, dependencyName, dependcyVersion);
            }

            return dependencies;
        }

        private static void DfsGetDependencies()
        {
            var stack = new Stack<string>(Packages);
            while (stack.Count > 0)
            {
                var packageName = stack.Pop();
                Console.WriteLine($"Processing {packageName}");
                var dependencies = QueryDependencies(packageName);
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

        private static void DistinctFileOutput(string fileName)
        {
            SortedSet<string> missingVersions = new();
            using StreamReader sr = new(fileName);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                missingVersions.Add(line);
            }
            sr.Close();
            using StreamWriter sw = new(fileName);
            foreach (var missingVersion in missingVersions)
            {
                sw.WriteLine(missingVersion);
            }
        }

        private static void GeneratePackageDependencyCSV()
        {
            using StreamWriter sw = new(PackageDependencyCSVFile);
            sw.WriteLine("packageName,packageVersion,targetFramework,dependencyName,dependencyVersion");
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

        static void Main()
        {
            InitializePackages();
            GeneratePackageDependencyCSV();
            DfsGetDependencies();
            GenerateDllInfoCSV();
            DistinctFileOutput(PackageVersionMissingFile);
            DistinctFileOutput(VersionConflictsFile);
            DistinctFileOutput(PackageDependencyCSVFile);
        }
    }
}