using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Kusto.Cloud.Platform.Utils;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Linq;
using Kusto.Data.Net.Client;

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
        private const string DefaultRootFile = "roots.txt";
        private const string DefaultPackagesPropsFile = "Packages.props";

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
                if (match.Success) {
                    var packageNameStart = line.NthIndexOf('\"',1) + 1; // Nthndexof is 1-based
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
            while(reader.Read())
            {
                dlls.Add($"{reader.GetString(0)}    {reader.GetString(2)}   {reader.GetString(3)}   {reader.GetString(4)}");
            }
            return dlls.ToList();
        }

        /*
         Known format:
        [version,version]
        (,)
        (version,version)
        [version,)
         */
        private static bool CheckVersionConflict(string dependencyVersionRange,string requiredVersion)
        {
            return true;
        }

        /*
         Query the package dependency table to get the dependencies of a package.
         */
        private static List<string> QueryDependencies(string packageName)
        {
            var dependencies = new List<string>();
            var version = GetVersionInPackagesProps(packageName);
            
            //Stopwatch sw = new();
            //sw.Start();
            var query = $"{PackageDependencyTable} | where Name == \"{packageName}\" and Version == \"{version}\"";
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
                //todo judge version conflict
                dependencies.Add(reader.GetString(3));
            }

            return dependencies;
        }

        private static void DfsGetDependencies()
        {
            var stack = new Stack<string>(Packages);
            while(stack.Count > 0)
            {
                var packageName = stack.Pop();
                Console.WriteLine($"Processing {packageName}");
                var dependencies = QueryDependencies(packageName);
                foreach(var dependency in dependencies)
                {
                    if (!Packages.Contains(dependency))
                    {
                        Packages.Add(dependency);
                        stack.Push(dependency);
                    }
                }
            }
        }

        static void Main()
        {
            InitializePackages();
            DfsGetDependencies();
            foreach(var package in Packages)
            {
                Console.WriteLine(package);
            }
            foreach(var package in Packages)
            {
                var dlls = GetDllsOfPackage(package);
                foreach(var dll in dlls)
                {
                    Console.WriteLine(dll);
                }
            }
        }

    }
}