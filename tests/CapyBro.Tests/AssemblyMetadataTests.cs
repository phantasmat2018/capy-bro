using System.Diagnostics;
using System.IO;
using System.Reflection;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests;

/// <summary>
/// FZ6-F4 / M40 — Pin the CapyBro brand metadata baked into the
/// published .exe's VersionInfo block.  Pre-fix the csproj was missing
/// the <c>&lt;Company&gt;</c> SDK property, so the apphost rewriter left
/// the CompanyName VersionInfo field empty (apphost.exe template
/// default).  Task Manager → Publisher and Properties → Details →
/// Company both showed a blank row on the running app, contradicting
/// the installer's <c>VIAddVersionKey "CompanyName" "CapyBro"</c> and
/// the installer Run-key DisplayName.  This test pins the contract so
/// a future csproj refactor that drops &lt;Company&gt; gets caught at
/// the test layer rather than in QA at release time.
/// </summary>
public class AssemblyMetadataTests
{
    [Fact]
    public void AssemblyCompany_IsCapyBro()
    {
        // The AssemblyCompanyAttribute is the source-of-truth at the
        // managed-assembly level — the SDK generates it from <Company>
        // in the csproj and the same value is propagated into the
        // apphost.exe's native VersionInfo CompanyName field.
        var attr = typeof(App).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>();
        attr.Should().NotBeNull("the csproj must declare <Company> so the SDK emits AssemblyCompanyAttribute");
        attr!.Company.Should().Be("CapyBro");
    }

    [Fact]
    public void AssemblyProduct_IsCapyBro()
    {
        var attr = typeof(App).Assembly.GetCustomAttribute<AssemblyProductAttribute>();
        attr.Should().NotBeNull();
        attr!.Product.Should().Be("CapyBro");
    }

    [Fact]
    public void AssemblyTitle_IsCapyBro()
    {
        // AssemblyTitleAttribute drives FileVersionInfo.FileDescription —
        // the column shown next to "CapyBro.exe" in Task Manager's
        // Details view (renamed 2026-05-12 from CapyBro.exe).
        var attr = typeof(App).Assembly.GetCustomAttribute<AssemblyTitleAttribute>();
        attr.Should().NotBeNull();
        attr!.Title.Should().Be("CapyBro");
    }

    [Fact]
    public void NativeExeVersionInfo_WhenAvailable_CarriesCapyBroBranding()
    {
        // The apphost shim (CapyBro.exe — renamed 2026-05-12 from
        // CapyBro.exe) embeds the native VersionInfo Windows
        // actually reads for Task Manager / Properties → Details.
        // We probe for it at the SDK output location relative to the
        // test assembly — it lives there in the standard build flow
        // because the test project ProjectReference's the app project.
        // If the exe is missing (e.g. running tests against a fresh
        // check-out before `dotnet build` of the app project), skip
        // rather than fail — the AssemblyCompany test above already
        // pins the source-of-truth, and CI rebuilds both projects.
        var exe = LocateBuiltExe();
        if (exe is null)
        {
            return;
        }

        var info = FileVersionInfo.GetVersionInfo(exe);
        info.CompanyName.Should().Be("CapyBro");
        info.ProductName.Should().Be("CapyBro");
        info.FileDescription.Should().Be("CapyBro");
    }

    private static string? LocateBuiltExe()
    {
        // Walk up from the test assembly's directory looking for the
        // src/CapyBro/bin/.../CapyBro.exe path (renamed
        // 2026-05-12 from CapyBro.exe).  Robust against running
        // from Debug or Release configs.  Falls back to the old name
        // for repos that still build the pre-rename binary so the test
        // doesn't bisect-fail on history checkouts before the rename
        // commit.
        var testDir = new DirectoryInfo(AppContext.BaseDirectory);
        string[] candidates = ["CapyBro.exe", "CapyBro.exe"];
        for (var dir = testDir; dir is not null; dir = dir.Parent)
        {
            foreach (var exeName in candidates)
            {
                var probe = Path.Combine(
                    dir.FullName,
                    "src",
                    "CapyBro",
                    "bin",
                    testDir.Parent?.Name ?? "Release",
                    "net8.0-windows",
                    exeName);
                if (File.Exists(probe))
                {
                    return probe;
                }
            }
        }

        return null;
    }
}
