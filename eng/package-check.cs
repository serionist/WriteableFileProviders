#:property RestorePackagesWithLockFile=false
#:property PublishAot=false
// The package check: dotnet run eng/package-check.cs -- <folder with the packed packages> <version>
//
// Every package releases together, at one version. release.yml runs this on what it packed before anything is
// pushed: a .nupkg and a .snupkg for every project under src/, nothing else, each at <version> (the tag's version), and
// every dependency of one of our packages on another exactly [<version>, <next major>.0.0-alpha)
// (src/Directory.Build.targets), so a provider never pulls in another release's Common, and a consumer who mixes
// majors gets a restore warning. It fails on a missing or extra package, another version, a package other than Common
// that does not depend on Common, a dependency on a WriteableFileProviders package with another range, or a version
// nuget.org would refuse.
using System.IO.Compression;
using System.Xml.Linq;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run eng/package-check.cs -- <packages folder> <version>");
    return 2;
}
var folder = args[0];
var version = args[1];
const string Prefix = "WriteableFileProviders.";
const string Common = "WriteableFileProviders.Common";
if (!int.TryParse(version.Split('.')[0], out var major))
{
    Console.Error.WriteLine($"'{version}' is not a version: its major is not a number.");
    return 2;
}
// Our packages depend on each other from this version up to, not including, the next major and its prereleases
// (-alpha sorts below every prerelease we make), which they never load against.
var expectedRange = $"[{version}, {major + 1}.0.0-alpha)";

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var projects = Directory.GetDirectories(Path.Combine(repoRoot, "src")).Select(Path.GetFileName).OfType<string>()
    .Where(name => File.Exists(Path.Combine(repoRoot, "src", name, name + ".csproj")))
    .Order(StringComparer.Ordinal).ToList();

var failures = new List<string>();
var packages = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.nupkg") : [];
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var file in packages.Order(StringComparer.Ordinal))
{
    var nuspec = ReadNuspec(file);
    var metadata = nuspec.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
        ?? throw new InvalidDataException($"{file} has no nuspec metadata");
    var id = Value(metadata, "id");
    var packageVersion = Value(metadata, "version");
    seen.Add(id);
    Console.WriteLine($"{id} {packageVersion}");

    if (!projects.Contains(id, StringComparer.Ordinal))
        failures.Add($"{Path.GetFileName(file)}: {id} is no project under src/");
    if (!string.Equals(packageVersion, version, StringComparison.OrdinalIgnoreCase))
        failures.Add($"{id} is {packageVersion}, not {version}");
    if (!File.Exists(Path.Combine(folder, $"{id}.{packageVersion}.snupkg")))
        failures.Add($"{id} {packageVersion} has no symbol package");
    if (GalleryRefusal(packageVersion) is { } versionRefusal)
        failures.Add($"{id} {packageVersion}: nuget.org refuses the version: {versionRefusal}");
    foreach (var dependencyElement in metadata.Descendants().Where(e => e.Name.LocalName == "dependency"))
    {
        var range = (string?)dependencyElement.Attribute("version") ?? "";
        var on = $"{id} depends on {(string?)dependencyElement.Attribute("id")} {range}";
        foreach (var bound in range.Trim('[', ']', '(', ')').Split(',').Select(b => b.Trim()).Where(b => b.Length > 0))
        {
            if (bound.Contains('+', StringComparison.Ordinal))
                failures.Add($"{on}: nuget.org refuses build metadata in a dependency");
            else if (GalleryRefusal(bound) is { } boundRefusal)
                failures.Add($"{on}: nuget.org refuses {bound}: {boundRefusal}");
        }
    }

    var dependencies = metadata.Descendants().Where(e => e.Name.LocalName == "dependency")
        .Select(e => (Id: (string?)e.Attribute("id") ?? "", Range: (string?)e.Attribute("version") ?? ""))
        .Where(d => d.Id.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    foreach (var (dependency, range) in dependencies)
    {
        Console.WriteLine($"  depends on {dependency} {range}");
        if (!string.Equals(Normalized(range), expectedRange, StringComparison.OrdinalIgnoreCase))
            failures.Add($"{id} depends on {dependency} {range}, not {expectedRange}");
    }
    if (id != Common && !dependencies.Any(d => d.Id == Common))
        failures.Add($"{id} does not depend on {Common}");
}
foreach (var missing in projects.Where(p => !seen.Contains(p)))
    failures.Add($"{missing} was not packed");

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    failures.ForEach(f => Console.Error.WriteLine("FAIL " + f));
    return 1;
}
Console.WriteLine();
Console.WriteLine($"Package check passed: {packages.Length} packages at {version}.");
return 0;

static XDocument ReadNuspec(string package)
{
    using var zip = ZipFile.OpenRead(package);
    var entry = zip.Entries.SingleOrDefault(e => !e.FullName.Contains('/', StringComparison.Ordinal)
        && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"{package} has no nuspec");
    using var stream = entry.Open();
    return XDocument.Load(stream);
}

static string Value(XElement metadata, string name) =>
    metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value
        ?? throw new InvalidDataException($"the nuspec has no {name}");

// Why nuget.org would refuse a version, or null. Its ManifestValidator takes a SemVer 2 version (a dotted prerelease
// label, or build metadata), and any other only if NuGet 2.x clients can read it: up to four numbers and at most one
// prerelease label that starts with a letter (NuGetGallery's NuGetVersionExtensions.IsValidVersionForLegacyClients).
static string? GalleryRefusal(string version)
{
    var dash = version.IndexOf('-', StringComparison.Ordinal);
    var label = dash < 0 ? "" : version[(dash + 1)..];
    if (label.Contains('.', StringComparison.Ordinal) || version.Contains('+', StringComparison.Ordinal))
        return null;
    return Gallery.LegacyVersion().IsMatch(version)
        ? null
        : "neither SemVer 2 nor readable by NuGet 2.x clients (a single prerelease label must start with a letter)";
}

// A NuGet version range written with one space after its comma and none elsewhere, as NuGet writes it.
static string Normalized(string range) =>
    string.Join(", ", range.Replace(" ", "", StringComparison.Ordinal).Split(','));

static string FindRepoRoot(string from)
{
    for (var dir = new DirectoryInfo(from); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "WriteableFileProviders.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Run the check from inside the repo.");
}

// NuGetGallery's SemanticVersionRegex: the versions NuGet 2.x clients read.
internal static partial class Gallery
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(\s*\.\s*\d+){0,3}(-[a-z][0-9a-z-]*)?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    public static partial System.Text.RegularExpressions.Regex LegacyVersion();
}
