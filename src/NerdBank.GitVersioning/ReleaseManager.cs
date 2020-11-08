﻿namespace Nerdbank.GitVersioning
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using LibGit2Sharp;
    using Nerdbank.GitVersioning.LibGit2;
    using Newtonsoft.Json;
    using Validation;
    using Version = System.Version;

    /// <summary>
    /// Methods for creating releases
    /// </summary>
    /// <remarks>
    /// This class authors git commits, branches, etc. and thus must use libgit2 rather than our internal managed implementation which is read-only.
    /// </remarks>
    public class ReleaseManager
    {
        /// <summary>
        /// Defines the possible errors that can occur when preparing a release
        /// </summary>
        public enum ReleasePreparationError
        {
            /// <summary>
            /// The project directory is not a git repository
            /// </summary>
            NoGitRepo,

            /// <summary>
            /// There are pending changes in the project directory
            /// </summary>
            UncommittedChanges,

            /// <summary>
            /// The "branchName" setting in "version.json" is invalid
            /// </summary>
            InvalidBranchNameSetting,

            /// <summary>
            /// version.json/version.txt not found
            /// </summary>
            NoVersionFile,

            /// <summary>
            /// Updating the version would result in a version lower than the previous version
            /// </summary>
            VersionDecrement,

            /// <summary>
            /// Cannot create a branch because it already exists
            /// </summary>
            BranchAlreadyExists,

            /// <summary>
            /// Cannot create a commit because user name and user email are not configured (either at the repo or global level)
            /// </summary>
            UserNotConfigured,

            /// <summary>
            /// HEAD is detached. A branch must be checked out first.
            /// </summary>
            DetachedHead,

            /// <summary>
            /// The versionIncrement setting cannot be applied to the current version.
            /// </summary>
            InvalidVersionIncrementSetting,
        }

        /// <summary>
        /// Exception indicating an error during preparation of a release
        /// </summary>
        public class ReleasePreparationException : Exception
        {
            /// <summary>
            /// Gets the error that occurred.
            /// </summary>
            public ReleasePreparationError Error { get; }

            /// <summary>
            /// Initializes a new instance of <see cref="ReleasePreparationException"/>
            /// </summary>
            /// <param name="error">The error that occurred.</param>
            public ReleasePreparationException(ReleasePreparationError error) => this.Error = error;
        }

        /// <summary>
        /// Encapsulates information on a release created through <see cref="ReleaseManager"/>.
        /// </summary>
        public class ReleaseInfo
        {
            /// <summary>
            /// Gets information on the 'current' branch, i.e. the branch the release was created from.
            /// </summary>
            public ReleaseBranchInfo CurrentBranch { get; }

            /// <summary>
            /// Gets information on the new branch created by <see cref="ReleaseManager"/>.
            /// </summary>
            /// <value>
            /// Information on the newly created branch as instance of <see cref="ReleaseBranchInfo"/> or <c>null</c>, if no new branch was created.
            /// </value>
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
            public ReleaseBranchInfo NewBranch { get; }

            /// <summary>
            /// Initializes a new instance of <see cref="ReleaseInfo"/>.
            /// </summary>
            /// <param name="currentBranch">Information on the branch the release was created from.</param>
            public ReleaseInfo(ReleaseBranchInfo currentBranch) : this(currentBranch, null)
            { }

            /// <summary>
            /// Initializes a new instance of <see cref="ReleaseInfo"/>.
            /// </summary>
            /// <param name="currentBranch">Information on the branch the release was created from.</param>
            /// <param name="newBranch">Information on the newly created branch.</param>
            [JsonConstructor]
            public ReleaseInfo(ReleaseBranchInfo currentBranch, ReleaseBranchInfo newBranch)
            {
                Requires.NotNull(currentBranch, nameof(currentBranch));
                // skip null check for newBranch, it is allowed to be null.

                this.CurrentBranch = currentBranch;
                this.NewBranch = newBranch;
            }
        }

        /// <summary>
        /// Encapsulates information on a branch created or updated by <see cref="ReleaseManager"/>.
        /// </summary>
        public class ReleaseBranchInfo
        {
            /// <summary>
            /// The name of the branch, e.g. <c>master</c>.
            /// </summary>
            public string Name { get; }

            /// <summary>
            /// The id of the branch's tip commit after the update.
            /// </summary>
            public string Commit { get; }

            /// <summary>
            /// The version configured in the branch's <c>version.json</c>.
            /// </summary>
            public SemanticVersion Version { get; }

            /// <summary>
            /// Initializes a new instance of <see cref="ReleaseBranchInfo"/>.
            /// </summary>
            /// <param name="name">The name of the branch.</param>
            /// <param name="commit">The id of the branch's tip.</param>
            /// <param name="version">The version configured in the branch's <c>version.json</c>.</param>
            public ReleaseBranchInfo(string name, string commit, SemanticVersion version)
            {
                Requires.NotNullOrWhiteSpace(name, nameof(name));
                Requires.NotNullOrWhiteSpace(commit, nameof(commit));
                Requires.NotNull(version, nameof(version));

                this.Name = name;
                this.Commit = commit;
                this.Version = version;
            }
        }

        /// <summary>
        /// Enumerates the output formats supported by <see cref="ReleaseManager"/>.
        /// </summary>
        public enum ReleaseManagerOutputMode
        {
            /// <summary>
            /// Use unstructured text output.
            /// </summary>
            Text = 0,
            /// <summary>
            /// Output information about the release as JSON.
            /// </summary>
            Json = 1
        }


        private readonly TextWriter stdout;
        private readonly TextWriter stderr;

        /// <summary>
        /// Initializes a new instance of <see cref="ReleaseManager"/>.
        /// </summary>
        /// <param name="outputWriter">The <see cref="TextWriter"/> to write output to (e.g. <see cref="Console.Out" />).</param>
        /// <param name="errorWriter">The <see cref="TextWriter"/> to write error messages to (e.g. <see cref="Console.Error" />).</param>
        public ReleaseManager(TextWriter outputWriter = null, TextWriter errorWriter = null)
        {
            this.stdout = outputWriter ?? TextWriter.Null;
            this.stderr = errorWriter ?? TextWriter.Null;
        }

        /// <summary>
        /// Prepares a release for the specified directory by creating a release branch and incrementing the version in the current branch.
        /// </summary>
        /// <exception cref="ReleasePreparationException">Thrown when the release could not be created.</exception>
        /// <param name="projectDirectory">
        /// The path to the directory which may (or its ancestors may) define the version file.
        /// </param>
        /// <param name="releaseUnstableTag">
        /// The prerelease tag to add to the version on the release branch. Pass <c>null</c> to omit/remove the prerelease tag.
        /// The leading hyphen may be specified or omitted.
        /// </param>
        /// <param name="nextVersion">
        /// The next version to save to the version file on the current branch. Pass <c>null</c> to automatically determine the next
        /// version based on the current version and the <c>versionIncrement</c> setting in <c>version.json</c>.
        /// Parameter will be ignored if the current branch is a release branch.
        /// </param>
        /// <param name="versionIncrement">
        /// The increment to apply in order to determine the next version on the current branch.
        /// If specified, value will be used instead of the increment specified in <c>version.json</c>.
        /// Parameter will be ignored if the current branch is a release branch.
        /// </param>
        /// <param name="outputMode">
        /// The output format to use for writing to stdout.
        /// </param>
        public void PrepareRelease(string projectDirectory, string releaseUnstableTag = null, Version nextVersion = null, VersionOptions.ReleaseVersionIncrement? versionIncrement = null, ReleaseManagerOutputMode outputMode = default)
        {
            Requires.NotNull(projectDirectory, nameof(projectDirectory));

            // open the git repository
            var repository = this.GetRepository(projectDirectory);

            if (repository.Info.IsHeadDetached)
            {
                this.stderr.WriteLine("Detached head. Check out a branch first.");
                throw new ReleasePreparationException(ReleasePreparationError.DetachedHead);
            }

            // get the current version
            var versionOptions = VersionFile.GetVersion(projectDirectory);
            if (versionOptions == null)
            {
                this.stderr.WriteLine($"Failed to load version file for directory '{projectDirectory}'.");
                throw new ReleasePreparationException(ReleasePreparationError.NoVersionFile);
            }

            var releaseBranchName = this.GetReleaseBranchName(versionOptions);
            var originalBranchName = repository.Head.FriendlyName;
            var releaseVersion = string.IsNullOrEmpty(releaseUnstableTag)
                ? versionOptions.Version.WithoutPrepreleaseTags()
                : versionOptions.Version.SetFirstPrereleaseTag(releaseUnstableTag);

            // check if the current branch is the release branch
            if (string.Equals(originalBranchName, releaseBranchName, StringComparison.OrdinalIgnoreCase))
            {
                if (outputMode == ReleaseManagerOutputMode.Text)
                {
                    this.stdout.WriteLine($"{releaseBranchName} branch advanced from {versionOptions.Version} to {releaseVersion}.");
                }
                else
                {
                    var releaseInfo = new ReleaseInfo(new ReleaseBranchInfo(releaseBranchName, repository.Head.Tip.Id.ToString(), releaseVersion));
                    this.WriteToOutput(releaseInfo);
                }
                this.UpdateVersion(projectDirectory, repository, versionOptions.Version, releaseVersion);
                return;
            }

            var nextDevVersion = this.GetNextDevVersion(versionOptions, nextVersion, versionIncrement);

            // check if the release branch already exists
            if (repository.Branches[releaseBranchName] != null)
            {
                this.stderr.WriteLine($"Cannot create branch '{releaseBranchName}' because it already exists.");
                throw new ReleasePreparationException(ReleasePreparationError.BranchAlreadyExists);
            }

            // create release branch and update version
            var releaseBranch = repository.CreateBranch(releaseBranchName);
            Commands.Checkout(repository, releaseBranch);
            this.UpdateVersion(projectDirectory, repository, versionOptions.Version, releaseVersion);

            if (outputMode == ReleaseManagerOutputMode.Text)
            {
                this.stdout.WriteLine($"{releaseBranchName} branch now tracks v{releaseVersion} stabilization and release.");
            }

            // update version on main branch
            Commands.Checkout(repository, originalBranchName);
            this.UpdateVersion(projectDirectory, repository, versionOptions.Version, nextDevVersion);

            if (outputMode == ReleaseManagerOutputMode.Text)
            {
                this.stdout.WriteLine($"{originalBranchName} branch now tracks v{nextDevVersion} development.");
            }

            // Merge release branch back to main branch
            var mergeOptions = new MergeOptions()
            {
                CommitOnSuccess = true,
                MergeFileFavor = MergeFileFavor.Ours,
            };
            repository.Merge(releaseBranch, this.GetSignature(repository), mergeOptions);

            if (outputMode == ReleaseManagerOutputMode.Json)
            {
                var originalBranchInfo = new ReleaseBranchInfo(originalBranchName, repository.Head.Tip.Sha, nextDevVersion);
                var releaseBranchInfo = new ReleaseBranchInfo(releaseBranchName, repository.Branches[releaseBranchName].Tip.Id.ToString(), releaseVersion);
                var releaseInfo = new ReleaseInfo(originalBranchInfo, releaseBranchInfo);

                this.WriteToOutput(releaseInfo);
            }
        }

        private string GetReleaseBranchName(VersionOptions versionOptions)
        {
            Requires.NotNull(versionOptions, nameof(versionOptions));

            var branchNameFormat = versionOptions.ReleaseOrDefault.BranchNameOrDefault;

            // ensure there is a '{version}' placeholder in the branch name
            if (string.IsNullOrEmpty(branchNameFormat) || !branchNameFormat.Contains("{version}"))
            {
                this.stderr.WriteLine($"Invalid 'branchName' setting '{branchNameFormat}'. Missing version placeholder '{{version}}'.");
                throw new ReleasePreparationException(ReleasePreparationError.InvalidBranchNameSetting);
            }

            // replace the "{version}" placeholder with the actual version
            return branchNameFormat.Replace("{version}", versionOptions.Version.Version.ToString());
        }

        private void UpdateVersion(string projectDirectory, Repository repository, SemanticVersion oldVersion, SemanticVersion newVersion)
        {
            Requires.NotNull(projectDirectory, nameof(projectDirectory));
            Requires.NotNull(repository, nameof(repository));

            var signature = this.GetSignature(repository);
            var versionOptions = VersionFile.GetVersion(repository, projectDirectory);

            if (IsVersionDecrement(oldVersion, newVersion))
            {
                this.stderr.WriteLine($"Cannot change version from {oldVersion} to {newVersion} because {newVersion} is older than {oldVersion}.");
                throw new ReleasePreparationException(ReleasePreparationError.VersionDecrement);
            }

            if (!EqualityComparer<SemanticVersion>.Default.Equals(versionOptions.Version, newVersion))
            {
                if (versionOptions.VersionHeightPosition.HasValue && SemanticVersion.WillVersionChangeResetVersionHeight(versionOptions.Version, newVersion, versionOptions.VersionHeightPosition.Value))
                {
                    // The version will be reset by this change, so remove the version height offset property.
                    versionOptions.VersionHeightOffset = null;
                }

                versionOptions.Version = newVersion;
                var filePath = VersionFile.SetVersion(projectDirectory, versionOptions, includeSchemaProperty: true);

                Commands.Stage(repository, filePath);

                // Author a commit only if we effectively changed something.
                if (!repository.Head.Tip.Tree.Equals(repository.Index.WriteToTree()))
                {
                    repository.Commit($"Set version to '{versionOptions.Version}'", signature, signature, new CommitOptions() { AllowEmptyCommit = false });
                }
            }
        }

        private Signature GetSignature(Repository repository)
        {
            var signature = repository.Config.BuildSignature(DateTimeOffset.Now);
            if (signature == null)
            {
                this.stderr.WriteLine("Cannot create commits in this repo because git user name and email are not configured.");
                throw new ReleasePreparationException(ReleasePreparationError.UserNotConfigured);
            }

            return signature;
        }

        private Repository GetRepository(string projectDirectory)
        {
            // open git repo and use default configuration (in order to commit we need a configured user name and email
            // which is most likely configured on a user/system level rather than the repo level
            var repository = GitExtensions.OpenGitRepo(projectDirectory, useDefaultConfigSearchPaths: true);
            if (repository == null)
            {
                this.stderr.WriteLine($"No git repository found above directory '{projectDirectory}'.");
                throw new ReleasePreparationException(ReleasePreparationError.NoGitRepo);
            }

            // abort if there are any pending changes
            if (repository.RetrieveStatus().IsDirty)
            {
                this.stderr.WriteLine($"Uncommitted changes in directory '{projectDirectory}'.");
                throw new ReleasePreparationException(ReleasePreparationError.UncommittedChanges);
            }

            // check if repo is configured so we can create commits
            _ = this.GetSignature(repository);

            return repository;
        }

        private static bool IsVersionDecrement(SemanticVersion oldVersion, SemanticVersion newVersion)
        {
            if (newVersion.Version > oldVersion.Version)
            {
                return false;
            }
            else if (newVersion.Version == oldVersion.Version)
            {
                return string.IsNullOrEmpty(oldVersion.Prerelease) &&
                      !string.IsNullOrEmpty(newVersion.Prerelease);
            }
            else
            {
                // newVersion.Version < oldVersion.Version
                return true;
            }
        }

        private SemanticVersion GetNextDevVersion(VersionOptions versionOptions, Version nextVersionOverride, VersionOptions.ReleaseVersionIncrement? versionIncrementOverride)
        {
            var currentVersion = versionOptions.Version;

            SemanticVersion nextDevVersion;
            if (nextVersionOverride != null)
            {
                nextDevVersion = new SemanticVersion(nextVersionOverride, currentVersion.Prerelease, currentVersion.BuildMetadata);
            }
            else
            {
                // Determine the increment to use:
                // Use parameter versionIncrementOverride if it has a value, otherwise use setting from version.json.
                var versionIncrement = versionIncrementOverride ?? versionOptions.ReleaseOrDefault.VersionIncrementOrDefault;

                // The increment is only valid if the current version has the required precision:
                //  - increment settings "Major" and "Minor" are always valid.
                //  - increment setting "Build" is only valid if the version has at lease three segments.
                var isValidIncrement = versionIncrement != VersionOptions.ReleaseVersionIncrement.Build ||
                                       versionOptions.Version.Version.Build >= 0;

                if (!isValidIncrement)
                {
                    this.stderr.WriteLine($"Cannot apply version increment 'build' to version '{versionOptions.Version}' because it only has major and minor segments.");
                    throw new ReleasePreparationException(ReleasePreparationError.InvalidVersionIncrementSetting);
                }

                nextDevVersion = currentVersion.Increment(versionIncrement);
            }

            // return next version with prerelease tag specified in version.json
            return nextDevVersion.SetFirstPrereleaseTag(versionOptions.ReleaseOrDefault.FirstUnstableTagOrDefault);
        }

        private void WriteToOutput(ReleaseInfo releaseInfo)
        {
            var json = JsonConvert.SerializeObject(releaseInfo, Formatting.Indented, new SemanticVersionJsonConverter());
            this.stdout.WriteLine(json);
        }
    }
}
