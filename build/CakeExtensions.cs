using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNetCore;
using Cake.Core;
using Cake.Core.IO;
using Cake.Core.Tooling;

namespace Build;

public static class DotNetCustomAliases
{
    public static void DotNetAddReference(this ICakeContext context, FilePath project, FilePath referencedProject)
    {
        var runner = new DotNetAddReferenceRunner(context.FileSystem, context.Environment, context.ProcessRunner, context.Tools);
        runner.AddReference(project, referencedProject, new IgnoredDotNetSettings());
    }

    public static void DotNetRemoveReference(this ICakeContext context, FilePath project, FilePath referencedProject)
    {
        var runner = new DotNetRemoveReferenceRunner(context.FileSystem, context.Environment, context.ProcessRunner, context.Tools);
        runner.RemoveReference(project, referencedProject, new IgnoredDotNetSettings());
    }

    public static void DotNetAddPackage(this ICakeContext context, FilePath project, string package, string? version = null, string[]? sources = null, bool preRelease = false)
    {
        var runner = new DotNetAddPackageRunner(context.FileSystem, context.Environment, context.ProcessRunner, context.Tools);
        runner.AddPackage(project, package, version, sources, preRelease, new IgnoredDotNetSettings());
    }

    public static void DotNetRemovePackage(this ICakeContext context, FilePath project, string package)
    {
        var runner = new DotNetRemovePackageRunner(context.FileSystem, context.Environment, context.ProcessRunner, context.Tools);
        runner.RemovePackage(project, package, new IgnoredDotNetSettings());
    }
}

public sealed class IgnoredDotNetSettings : DotNetSettings
{
}

public sealed class DotNetAddReferenceRunner : DotNetCoreTool<IgnoredDotNetSettings>
{
    public DotNetAddReferenceRunner(IFileSystem fileSystem, ICakeEnvironment environment, IProcessRunner processRunner, IToolLocator tools)
        : base(fileSystem, environment, processRunner, tools)
    {
    }

    public void AddReference(FilePath project, FilePath referencedProject, IgnoredDotNetSettings settings)
    {
        var builder = new ProcessArgumentBuilder();

        builder.Append("add");
        builder.AppendQuoted(project.FullPath);
        builder.Append("reference");
        builder.AppendQuoted(referencedProject.FullPath);

        this.RunCommand(settings, builder);
    }
}

public sealed class DotNetRemoveReferenceRunner : DotNetCoreTool<IgnoredDotNetSettings>
{
    public DotNetRemoveReferenceRunner(IFileSystem fileSystem, ICakeEnvironment environment, IProcessRunner processRunner, IToolLocator tools)
        : base(fileSystem, environment, processRunner, tools)
    {
    }

    public void RemoveReference(FilePath project, FilePath referencedProject, IgnoredDotNetSettings settings)
    {
        var builder = new ProcessArgumentBuilder();

        builder.Append("remove");
        builder.AppendQuoted(project.FullPath);
        builder.Append("reference");
        builder.AppendQuoted(referencedProject.FullPath);

        this.RunCommand(settings, builder);
    }
}

public sealed class DotNetAddPackageRunner : DotNetCoreTool<IgnoredDotNetSettings>
{
    public DotNetAddPackageRunner(IFileSystem fileSystem, ICakeEnvironment environment, IProcessRunner processRunner, IToolLocator tools)
        : base(fileSystem, environment, processRunner, tools)
    {
    }

    public void AddPackage(FilePath project, string packageName, string? version, string[]? sources, bool preRelease, IgnoredDotNetSettings settings)
    {
        var builder = new ProcessArgumentBuilder();

        builder.Append("add");
        builder.AppendQuoted(project.FullPath);
        builder.Append("package");
        builder.Append(packageName);

        if (version is not null)
        {
            builder.Append("--version");
            builder.Append(version);
        }
        else if (preRelease)
        {
            builder.Append("--prerelease");
        }

        if (sources is not null)
        {
            foreach (var source in sources)
            {
                builder.Append("--source");
                builder.Append(source);
            }
        }

        this.RunCommand(settings, builder);
    }
}

public sealed class DotNetRemovePackageRunner : DotNetCoreTool<IgnoredDotNetSettings>
{
    public DotNetRemovePackageRunner(IFileSystem fileSystem, ICakeEnvironment environment, IProcessRunner processRunner, IToolLocator tools)
        : base(fileSystem, environment, processRunner, tools)
    {
    }

    public void RemovePackage(FilePath project, string packageName, IgnoredDotNetSettings settings)
    {
        var builder = new ProcessArgumentBuilder();

        builder.Append("remove");
        builder.AppendQuoted(project.FullPath);
        builder.Append("package");
        builder.Append(packageName);

        this.RunCommand(settings, builder);
    }
}