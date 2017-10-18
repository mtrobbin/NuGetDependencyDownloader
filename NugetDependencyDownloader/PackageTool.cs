using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NuGet;

namespace NuGetDependencyDownloader
{
    // Good examples
    // http://blog.nuget.org/20130520/Play-with-packages.html

    public class PackageTool
    {
        private IList<IPackage> _packages = new List<IPackage>();

        public Func<bool> StopRequested { get; set; }
        public Action<string> Progress { get; set; }

        public void ProcessPackage(string packageId, string packageVersion, bool preRelease, string downloadDirectory, IList<string> targetFrameworks)
        {
            CollectPackages(packageId, packageVersion, preRelease, targetFrameworks);

            if (StopRequested())
            {
                Progress("Stopped.");
                return;
            }

            Progress(string.Format("{0} packages to download.", _packages.Count));

            DownloadPackages(downloadDirectory);

            if (StopRequested())
            {
                Progress("Stopped.");
                return;
            }

            Progress("Done.");
        }

        private void CollectPackages(string packageId, string packageVersion, bool preRelease, IList<string> targetFrameworks)
        {
            IPackage package;
            if (string.IsNullOrWhiteSpace(packageVersion))
            {
                package = GetLatestPackage(packageId, preRelease);
            }
            else
            {
                SemanticVersion version;
                try
                {
                    version = SemanticVersion.Parse(packageVersion);
                }
                catch (ArgumentException)
                {
                    Progress("Unable to parse package version.");
                    return;
                }
                package = GetPackages(packageId, preRelease)
                    .Where(o => o.Version == version)
                    .FirstOrDefault();
            }

            if (package == null)
            {
                Progress("Package not found.");
                return;
            }

            _packages.Add(package);
            Progress(package.GetFullName());
            LoadDependencies(package, preRelease, targetFrameworks);
        }
        
        private void LoadDependencies(IPackage package, bool preRelease, IList<string> targetFrameworks)
        {
            if (package.DependencySets != null)
            {
                var dependencies = package.DependencySets.Where(d => (d.TargetFramework == null) || targetFrameworks.Contains(d.TargetFramework.Identifier)).SelectMany(o => o.Dependencies.Select(x => new Dependency { Id = x.Id, VersionSpec = x.VersionSpec })).ToList();

                foreach (Dependency dependency in dependencies)
                {
                    if (StopRequested())
                        return;

                    IQueryable<IPackage> packages = GetPackages(dependency.Id, preRelease);
                    IPackage depPackage = GetRangedPackageVersion(packages, dependency.VersionSpec);
                    Progress(string.Format("{0} -> {1}", package.GetFullName(), depPackage.GetFullName()));

                    if (!IsPackageKnown(depPackage))
                    {
                        _packages.Add(depPackage);
                        LoadDependencies(depPackage, preRelease, targetFrameworks);
                    }
                }
            }
        }

        private void DownloadPackages(string downloadDirectory)
        {
            if (!Directory.Exists(downloadDirectory))
                Directory.CreateDirectory(downloadDirectory);

            foreach (IPackage package in _packages)
            {
                if (StopRequested())
                    return;

                string fileName = Path.Combine(downloadDirectory, string.Format("{0}.{1}.nupkg", package.Id, package.Version));

                if (File.Exists(fileName))
                {
                    Progress(string.Format("{0} already downloaded.", fileName));
                    continue;
                }

                Progress(string.Format("downloading {0} {1}", package.Id, package.Version));
                using (var client = new WebClient())
                {
                    DataServicePackage dsp = (DataServicePackage)package;
                    client.DownloadFile(dsp.DownloadUrl, fileName);
                }
            }
        }

        private IQueryable<IPackage> GetPackages(string packageId, bool includePreRelease)
        {
            IPackageRepository repo = PackageRepositoryFactory.Default.CreateRepository("https://packages.nuget.org/api/v2");
            IQueryable<IPackage> packages = repo.FindPackagesById(packageId).AsQueryable();

            if (!includePreRelease)
            {
                packages = packages.Where(item => (item.IsReleaseVersion() == true));
            }

            return packages;
        }

        private IPackage GetLatestPackage(string packageId, bool includePrerelease)
        {
            IQueryable<IPackage> packages = GetPackages(packageId, includePrerelease);

            if (!includePrerelease)
            {
                packages = packages
                    .Where(item => item.IsReleaseVersion() == true)
                    .Where(o => o.IsLatestVersion);
            }

            IPackage latest = packages.OrderByDescending(o => o.Version).FirstOrDefault();

            return latest;
        }

        private IPackage GetRangedPackageVersion(IQueryable<IPackage> packages, IVersionSpec versionSpec)
        {
            if (versionSpec.MinVersion != null)
            {
                if (versionSpec.IsMinInclusive)
                {
                    packages = packages.Where(o => o.Version >= versionSpec.MinVersion);
                }
                else
                {
                    packages = packages.Where(o => o.Version > versionSpec.MinVersion);
                }
            }

            if (versionSpec.MaxVersion != null)
            {
                if (versionSpec.IsMaxInclusive)
                {
                    packages = packages.Where(o => o.Version <= versionSpec.MaxVersion);
                }
                else
                {
                    packages = packages.Where(o => o.Version < versionSpec.MaxVersion);
                }
            }

            IPackage package = packages
                .OrderByDescending(o => o.Version)
                .FirstOrDefault();

            return package;
        }

        private bool IsPackageKnown(IPackage package)
        {
            return _packages.Any(o => o.Title == package.Title && o.Version == package.Version);
        }

        private class Dependency
        {
            public string Id { get; set; }
            public IVersionSpec VersionSpec { get; set; }
        }
    }
}
