using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class RunSnapshotTests
    {
        [Fact]
        public void Capture_CopiesTargetsAndPasswordCandidates()
        {
            var task = new ArchiveTask { CurrentPath = @"C:\in\111.7z" };
            var targets = new List<ArchiveTask> { task };
            var candidates = new List<string?> { null, "pw" };
            var options = new OneClickRunOptions
            {
                PlacementMode = ArchiveFixer.Extraction.OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = @"D:\out",
                SourceHandling = SourceHandlingMode.DeleteInPlace
            };

            RunSnapshot snapshot = RunSnapshot.Capture(
                targets,
                options,
                spaceTightMode: true,
                spaceTightKeepSource: false,
                useSpecificExtraction: true,
                selectedOutputDirectory: @"D:\out",
                passwordCandidates: candidates);

            targets.Clear();
            candidates.Clear();

            Assert.Single(snapshot.Targets);
            Assert.Same(task, snapshot.Targets[0]);
            Assert.Equal(2, snapshot.PasswordCandidates.Count);
            Assert.Equal(string.Empty, snapshot.PasswordCandidates[0]);
            Assert.Equal("pw", snapshot.PasswordCandidates[1]);
            Assert.Equal(@"D:\out", snapshot.Options.CustomRoot);
            Assert.True(snapshot.SpaceTightMode);
            Assert.False(snapshot.SpaceTightKeepSource);
            Assert.True(snapshot.UseSpecificExtraction);

            var stop = new RunStopToken();
            var withStop = RunSnapshot.Capture(
                targets: Array.Empty<ArchiveTask>(),
                options: options,
                spaceTightMode: false,
                spaceTightKeepSource: false,
                useSpecificExtraction: false,
                selectedOutputDirectory: null,
                stopToken: stop);
            stop.RequestStop();
            Assert.True(withStop.StopToken.IsStopRequested);
        }
    }
}
