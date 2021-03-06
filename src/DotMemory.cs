using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Profiler.SelfApi.Impl;

namespace JetBrains.Profiler.SelfApi
{
  /// <summary>
  /// The API lets you initiate and control profiling sessions right from the code of your application.
  /// For example, this can be helpful for profiling the application on end-user desktops or production servers.
  /// The API uses the dotMemory.exe command-line profiler (the tool is downloaded automatically)
  /// </summary>
  /// <remarks>
  /// Use case 1: ad-hoc profiling <br/>
  /// * install the JetBrains.Profiler.SelfApi package to your project<br/>
  /// * to initialize the API, call DotMemory.EnsurePrerequisite()<br/>
  /// * to get just one memory snapshot, call DotMemory.GetSnapshotOnce<br/>
  /// * or in case you need several snapshots, call Attach/GetSnapshot*/Detach<br/>
  /// * deploy your application<br/>
  /// * reproduce the issue<br/>
  /// * investigate the generated workspace with snapshots using JetBrains dotMemory<br/>
  ///<br/>
  /// Use case 2: self-profiling as a part of troubleshooting on a production server<br/>
  /// * install the JetBrains.Profiler.SelfApi package to your project<br/>
  /// * in handler of awesome `Gather trouble report` action call DotMemory.EnsurePrerequisite()<br/>
  /// * to get a memory snapshot, call DotMemory.GetSnapshotOnce<br/>
  /// * include the generated workspace with snapshots into the report<br/>
  /// </remarks>
  [SuppressMessage("ReSharper", "UnusedMethodReturnValue.Local")]
  [SuppressMessage("ReSharper", "UnusedMember.Global")]
  [SuppressMessage("ReSharper", "UnusedType.Global")]
  public static class DotMemory
  {
    private const string MessageServicePrefix = "##dotMemory";
    private const string CltPresentableName = "dotMemory console profiler";

    /// <summary>
    /// The version of JetBrains.dotMemory.Console NuGet-package that must be downloaded.
    /// </summary>
    private static readonly NuGet.SemanticVersion NupkgVersion = new NuGet.SemanticVersion(2020, 2);

    /// <summary>
    /// Self-profiling configuration
    /// </summary>
    public sealed class Config : CommonConfig
    {
      internal string LogLevel;
      internal string WorkspaceFile;
      internal string WorkspaceDir;
      internal bool IsOpenDotMemory;
      internal bool IsOverwriteWorkspace;
      internal string OtherArguments;

      /// <summary>
      /// Specifies the path to the workspace file (snapshots storage).
      /// </summary>
      public Config SaveToFile(string filePath, bool overwrite = false)
      {
        if (WorkspaceDir != null)
          throw new InvalidOperationException("The SaveToFile and SaveToDir are mutually exclusive.");

        WorkspaceFile = filePath ?? throw new ArgumentNullException(nameof(filePath));
        IsOverwriteWorkspace = overwrite;
        return this;
      }

      /// <summary>
      /// Specifies the path to the workspace directory (filename will be auto-generated).
      /// </summary>
      public Config SaveToDir(string dirPath)
      {
        if (WorkspaceDir != null)
          throw new InvalidOperationException("The SaveToDir and SaveToFile are mutually exclusive.");

        WorkspaceDir = dirPath ?? throw new ArgumentNullException(nameof(dirPath));
        return this;
      }

      /// <summary>
      /// Specifies whether to open the generated workspace in JetBrains dotMemory.
      /// </summary>
      public Config OpenDotMemory()
      {
        IsOpenDotMemory = true;
        return this;
      }

      /// <summary>
      /// Sets the TRACE logging level.
      /// </summary>
      public Config UseLogLevelTrace()
      {
        LogLevel = "Trace";
        return this;
      }

      /// <summary>
      /// Sets the VERBOSE logging level.
      /// </summary>
      public Config UseLogLevelVerbose()
      {
        LogLevel = "Verbose";
        return this;
      }

      /// <summary>
      /// Appends an arbitrary argument to the command line as is (without any quoting, and so on).
      /// </summary>
      public Config WithCommandLineArgument(string argument)
      {
        if (argument == null) throw new ArgumentNullException(nameof(argument));

        if (OtherArguments != null)
          OtherArguments += " " + argument;
        else
          OtherArguments = argument;

        return this;
      }
    }

    private const int Timeout = 30000;
    private static readonly ConsoleToolRunner OurConsoleToolRunner = new ConsoleToolRunner(new Prerequisite());
    private static readonly object OurMutex = new object();
    private static Session _session;

    /// <summary>
    /// Makes sure that the dotMemory.exe command-line profiler is downloaded and is ready to use.
    /// </summary>
    /// <remarks>
    /// 1. Looks for `dotMemory.exe` in the same folder with the running assembly. Uses it if it's found.<br/>
    /// 2. Downloads `JetBrains.dotMemory.Console` NuGet package into the <paramref name="downloadTo"/>
    /// folder and uses the dotMemory.exe command-line profiler from this package. The package version is defined by <see cref="NupkgVersion"/>.
    /// The command-line profiler is saved to `{downloadTo}/dotMemory.{NupkgVersion}/dotMemory.exe`
    /// If the file exists, a new one is not downloaded.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="progress">Download progress callback from 0.0 to 100.0. If null, progress is not reported.</param>
    /// <param name="nugetUrl">URL of NuGet mirror. If null, www.nuget.org is used.</param>
    /// <param name="nugetApi">NuGet API version.</param>
    /// <param name="downloadTo">NuGet download destination folder. If null, %LocalAppData% is used.</param>
    public static Task EnsurePrerequisiteAsync(
      CancellationToken cancellationToken,
      IProgress<double> progress = null,
      Uri nugetUrl = null,
      NuGetApi nugetApi = NuGetApi.V3,
      string downloadTo = null)
    {
      lock (OurMutex)
        return OurConsoleToolRunner.EnsurePrerequisiteAsync(cancellationToken, progress, nugetUrl, nugetApi, downloadTo);
    }

    /// <summary>
    /// The shortcut for <c>EnsurePrerequisiteAsync(CancellationToken.None, progress, nugetUrl, prerequisitePath)</c>
    /// </summary>
    public static Task EnsurePrerequisiteAsync(
      IProgress<double> progress = null,
      Uri nugetUrl = null,
      NuGetApi nugetApi = NuGetApi.V3,
      string downloadTo = null)
    {
      return EnsurePrerequisiteAsync(CancellationToken.None, progress, nugetUrl, nugetApi, downloadTo);
    }

    /// <summary>
    /// The shortcut for <c>EnsurePrerequisiteAsync(CancellationToken.None, progress: null, nugetUrl, prerequisitePath).Wait()</c>
    /// </summary>
    public static void EnsurePrerequisite(
      Uri nugetUrl = null,
      NuGetApi nugetApi = NuGetApi.V3,
      string downloadTo = null)
    {
      EnsurePrerequisiteAsync(null, nugetUrl, nugetApi, downloadTo).Wait();
    }

    /// <summary>
    /// The shortcut for <see cref="Attach()"/>; <see cref="GetSnapshot"/>; <see cref="Detach"/>;
    /// </summary>
    /// <returns>Saved workspace file path.</returns>
    public static string GetSnapshotOnce()
    {
      return GetSnapshotOnce(new Config());
    }

    /// <summary>
    /// The shortcut for <see cref="Attach(Config)"/>; <see cref="GetSnapshot"/>; <see cref="Detach"/>;
    /// </summary>
    /// <returns>Saved workspace file path.</returns>
    public static string GetSnapshotOnce(Config config)
    {
      if (config == null) throw new ArgumentNullException(nameof(config));

      lock (OurMutex)
      {
        OurConsoleToolRunner.AssertIfReady();

        if (_session != null)
          throw new InvalidOperationException("The profiling session is active already: Attach() was called early.");

        // `get-snapshot` command doesn't support API mode
        config.IsUseApi = false;

        return RunConsole("get-snapshot --raw", config).AwaitFinished(-1).WorkspaceFile;
      }
    }

    /// <summary>
    /// Attaches dotMemory to the current process using the default API configuration.
    /// </summary>
    public static void Attach()
    {
      Attach(new Config());
    }

    /// <summary>
    /// Attaches dotMemory to the current process using the specified API configuration.
    /// </summary>
    public static void Attach(Config config)
    {
      if (config == null)
        throw new ArgumentNullException(nameof(config));
      Helper.CheckAttachCompatibility();
      lock (OurMutex)
      {
        OurConsoleToolRunner.AssertIfReady();

        if (_session != null)
          throw new InvalidOperationException("The profiling session is active still: forgot to call Detach()?");

        _session = RunConsole("attach -s", config).AwaitConnected(Timeout);
      }
    }

    /// <summary>
    /// Detaches dotMemory from the current process.
    /// </summary>
    /// <returns>Saved workspace file path.</returns>
    public static string Detach()
    {
      lock (OurMutex)
      {
        if (_session == null)
          throw new InvalidOperationException("The profiling session isn't active: forgot to call Attach()?");

        try
        {
          return _session.Detach().AwaitFinished(Timeout).WorkspaceFile;
        }
        finally
        {
          _session = null;
        }
      }
    }

    /// <summary>
    /// Gets a memory snapshot of the current process.
    /// </summary>
    /// <param name="name">Optional snapshot name.</param>
    public static void GetSnapshot(string name = null)
    {
      lock (OurMutex)
      {
        if (_session == null)
          throw new InvalidOperationException("The profiling session isn't active: forgot to call Attach()?");

        _session.GetSnapshot(name);
      }
    }

    private static string GetSaveToFilePath(Config config)
    {
      if (config.WorkspaceFile != null)
        return config.WorkspaceFile;

      var workspaceName = $"{Process.GetCurrentProcess().ProcessName}.{DateTime.Now:yyyy-MM-ddTHH-mm-ss.fff}.dmw";
      if (config.WorkspaceDir != null)
        return Path.Combine(config.WorkspaceDir, workspaceName);

      return Path.Combine(Path.GetTempPath(), workspaceName);
    }

    private static Session RunConsole(string command, Config config)
    {
      Trace.Verbose("DotMemory.RunConsole: Looking for runner...");

      var runnerPath = OurConsoleToolRunner.GetRunner();

      var workspaceFile = GetSaveToFilePath(config);

      var commandLine = new StringBuilder();

      if (config.LogLevel != null)
        commandLine.Append($"--log-level={config.LogLevel} ");

      if (config.LogFile != null)
        commandLine.Append($"\"--log-file={config.LogFile}\" ");

      commandLine.Append($"{command} {Process.GetCurrentProcess().Id} \"-f={workspaceFile}\"");

      if (config.IsOverwriteWorkspace)
        commandLine.Append(" --overwrite");

      if (config.IsOpenDotMemory)
        commandLine.Append(" --open-dotmemory");

      DynamicMemoryProfilerApi api = null;
      if (config.IsUseApi.HasValue)
      {
        if (config.IsUseApi.Value)
        {
          Trace.Info("DotMemory.RunConsole: force to use API");
          api = DynamicMemoryProfilerApi.TryCreate();
          if (api == null)
            throw new InvalidOperationException("Unable to load `JetBrains.Profiler.Api` assembly.");
        }
        else
        {
          Trace.Info("DotMemory.RunConsole: force to do not use API");
        }
      }
      else // auto mode
      {
        Trace.Info("DotMemory.RunConsole: auto API mode...");
        api = DynamicMemoryProfilerApi.TryCreate();
        Trace.Info(api != null
          ? "DotMemory.RunConsole: API assembly found, will use it"
          : "DotMemory.RunConsole: API assembly not found, will use service messages");
      }

      if (api != null)
        commandLine.Append(" --use-api");

      if (config.OtherArguments != null)
        commandLine.Append(' ').Append(config.OtherArguments);

      Trace.Info("DotMemory.RunConsole:\n  runner = `{0}`\n  arguments = `{1}`", runnerPath, commandLine);

      var consoleProfiler = new ConsoleProfiler(runnerPath, commandLine.ToString(), MessageServicePrefix, CltPresentableName, api != null ? (Func<bool>) api.IsReady : null);

      Trace.Verbose("DotMemory.RunConsole: Runner started.");

      return new Session(consoleProfiler, api, workspaceFile);
    }

    private sealed class Prerequisite : PrerequisiteBase
    {
      public Prerequisite() : base("dotMemory", NupkgVersion)
      {
      }

      protected override string GetRunnerName()
      {
        switch (Helper.Platform)
        {
        case PlatformId.Linux:
        case PlatformId.MacOs: return "dotMemory.sh";
        case PlatformId.Windows: return "dotMemory.exe";
        default: throw new ArgumentOutOfRangeException();
        }
      }

      protected override string GetPackageName()
      {
        return "JetBrains.dotMemory.Console";
      }

      protected override long GetEstimatedSize()
      {
        return 20 * 1024 * 1024;
      }
    }

    private sealed class Session
    {
      private readonly DynamicMemoryProfilerApi _profilerApi;
      private readonly ConsoleProfiler _consoleProfiler;

      public Session(ConsoleProfiler consoleProfiler, DynamicMemoryProfilerApi profilerApi, string workspaceFile)
      {
        _consoleProfiler = consoleProfiler;
        _profilerApi = profilerApi;

        WorkspaceFile = workspaceFile;
      }

      public string WorkspaceFile { get; }

      [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
      public Session Detach()
      {
        if (_profilerApi != null)
          _profilerApi.Detach();
        else
          _consoleProfiler.Send("disconnect");
        return this;
      }

      [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
      public Session GetSnapshot(string name)
      {
        if (_profilerApi != null)
          _profilerApi.GetSnapshot(name);
        else
        {
          _consoleProfiler.Send("get-snapshot", "name", name);
          _consoleProfiler.AwaitResponse("snapshot-saved", -1);
        }
        return this;
      }

      public Session AwaitConnected(int milliseconds)
      {
        _consoleProfiler.AwaitConnected(milliseconds);
        return this;
      }

      public Session AwaitFinished(int milliseconds)
      {
        _consoleProfiler.AwaitFinished(milliseconds);
        return this;
      }
    }
  }
}