﻿using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.Common;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Build2 = Microsoft.TeamFoundation.Build.WebApi;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public class BuildServer
    {
        private Uri _projectCollectionUrl;
        private VssCredentials _credential;
        private Guid _projectId;

        private Build2.BuildHttpClient BuildHttpClient { get; }

        public BuildServer(
            Uri projectCollection,
            VssCredentials credentials,
            Guid projectId)
        {
            ArgUtil.NotNull(projectCollection, nameof(projectCollection));
            ArgUtil.NotNull(credentials, nameof(credentials));
            ArgUtil.NotEmpty(projectId, nameof(projectId));

            _projectCollectionUrl = projectCollection;
            _credential = credentials;
            _projectId = projectId;

            BuildHttpClient = new Build2.BuildHttpClient(projectCollection, credentials, new VssHttpRetryMessageHandler(3));
        }

        public async Task<Build2.BuildArtifact> AssociateArtifact(
            int buildId,
            string name,
            string type,
            string data,
            Dictionary<string, string> propertiesDictionary,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Build2.BuildArtifact artifact = new Build2.BuildArtifact()
            {
                Name = name,
                Resource = new Build2.ArtifactResource()
                {
                    Data = data,
                    Type = type,
                    Properties = propertiesDictionary
                }
            };

            return await BuildHttpClient.CreateArtifactAsync(artifact, _projectId, buildId, cancellationToken);
        }

        public async Task<Build2.Build> UpdateBuildNumber(
            int buildId,
            string buildNumber,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Build2.Build build = new Build2.Build()
            {
                Id = buildId,
                BuildNumber = buildNumber,
                Project = new TeamProjectReference()
                {
                    Id = _projectId,
                },
            };

            return await BuildHttpClient.UpdateBuildAsync(build, _projectId, buildId, cancellationToken);
        }

        public async Task<IEnumerable<string>> AddBuildTag(
            int buildId,
            string buildTag,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await BuildHttpClient.AddBuildTagAsync(_projectId, buildId, buildTag, cancellationToken: cancellationToken);
        }
    }
}