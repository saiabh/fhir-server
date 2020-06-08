// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Health.Core;
using Microsoft.Health.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Resources
{
    public abstract class BaseResourceHandler
    {
        private readonly IResourceWrapperFactory _resourceWrapperFactory;
        private readonly ResourceIdProvider _resourceIdProvider;

        protected BaseResourceHandler(
            IFhirDataStore fhirDataStore,
            Lazy<IConformanceProvider> conformanceProvider,
            IResourceWrapperFactory resourceWrapperFactory,
            ResourceIdProvider resourceIdProvider,
            IFhirAuthorizationService authorizationService)
        {
            EnsureArg.IsNotNull(fhirDataStore, nameof(fhirDataStore));
            EnsureArg.IsNotNull(conformanceProvider, nameof(conformanceProvider));
            EnsureArg.IsNotNull(resourceWrapperFactory, nameof(resourceWrapperFactory));
            EnsureArg.IsNotNull(resourceIdProvider, nameof(resourceIdProvider));
            EnsureArg.IsNotNull(authorizationService, nameof(authorizationService));

            ConformanceProvider = conformanceProvider;
            AuthorizationService = authorizationService;
            FhirDataStore = fhirDataStore;
            _resourceWrapperFactory = resourceWrapperFactory;
            _resourceIdProvider = resourceIdProvider;
        }

        protected Lazy<IConformanceProvider> ConformanceProvider { get; }

        protected IFhirDataStore FhirDataStore { get; }

        protected IFhirAuthorizationService AuthorizationService { get; }

        protected ResourceWrapper CreateResourceWrapper(ResourceElement resource, bool deleted)
        {
            if (string.IsNullOrEmpty(resource.Id))
            {
                resource.UpdateId(_resourceIdProvider.Create());
            }

            // store with millisecond precision
            resource.UpdateLastUpdated(Clock.UtcNow.UtcDateTime.TruncateToMillisecond());

            ResourceWrapper resourceWrapper = _resourceWrapperFactory.Create(resource, deleted);

            return resourceWrapper;
        }
    }
}