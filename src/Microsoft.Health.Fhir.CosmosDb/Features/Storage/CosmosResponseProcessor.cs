﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.CosmosDb.Features.Queries;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.CosmosDb.Features.Metrics;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage
{
    public class CosmosResponseProcessor : ICosmosResponseProcessor
    {
        private readonly IFhirRequestContextAccessor _fhirRequestContextAccessor;
        private readonly IMediator _mediator;
        private readonly ILogger<CosmosResponseProcessor> _logger;

        public CosmosResponseProcessor(IFhirRequestContextAccessor fhirRequestContextAccessor, IMediator mediator, ILogger<CosmosResponseProcessor> logger)
        {
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(mediator, nameof(mediator));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _mediator = mediator;
            _logger = logger;
        }

        /// <summary>
        /// Adds request charge to the response headers and throws a <see cref="RequestRateExceededException"/>
        /// if the status code is 429.
        /// </summary>
        /// <param name="ex">The exception</param>
        public async Task ProcessException(Exception ex)
        {
            if (_fhirRequestContextAccessor.FhirRequestContext == null)
            {
                return;
            }

            EnsureArg.IsNotNull(ex, nameof(ex));

            if (ex is CosmosException dce)
            {
                await ProcessResponse(sessionToken: null, dce.RequestCharge, dce.StatusCode);

                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.Message.Contains("Invalid Continuation Token", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Core.Exceptions.RequestNotValidException(Core.Resources.InvalidContinuationToken);
                }
                else if (dce.StatusCode == HttpStatusCode.RequestEntityTooLarge
                         || (dce.StatusCode == HttpStatusCode.BadRequest && dce.Message.Contains("Request size is too large", StringComparison.OrdinalIgnoreCase)))
                {
                    // There are multiple known failures relating to RequestEntityTooLarge.
                    // 1. When the document size is ~2mb (just under or at the limit) it can make it into the stored proc and fail on create
                    // 2. Larger documents are rejected by CosmosDb with HttpStatusCode.RequestEntityTooLarge
                    throw new Core.Exceptions.RequestEntityTooLargeException();
                }
                else if (dce.StatusCode == HttpStatusCode.Forbidden && dce.GetSubStatusValue() == CosmosDbSubStatusValues.CustomerManagedKeyInaccessible)
                {
                    throw new Core.Exceptions.CustomerManagedKeyInaccessibleException();
                }
            }
        }

        public async Task<ItemResponse<T>> ProcessResponse<T>(ItemResponse<T> feedResponse)
        {
            await ProcessResponse(feedResponse.Headers.Session, feedResponse.RequestCharge, statusCode: null);
            return feedResponse;
        }

        public async Task<FeedResponse<T>> ProcessResponse<T>(FeedResponse<T> feedResponse)
        {
            await ProcessResponse(feedResponse.Headers.Session, feedResponse.RequestCharge, statusCode: null);
            return feedResponse;
        }

        public async Task<StoredProcedureExecuteResponse<T>> ProcessResponse<T>(StoredProcedureExecuteResponse<T> storedProcedureResponse)
        {
            await ProcessResponse(storedProcedureResponse.SessionToken, storedProcedureResponse.RequestCharge, storedProcedureResponse.StatusCode);
            return storedProcedureResponse;
        }

        /// <summary>
        /// Updates the request context with Cosmos DB info and updates response headers with the session token and request change values.
        /// </summary>
        /// <param name="sessionToken">THe session token</param>
        /// <param name="responseRequestCharge">The request charge.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public async Task ProcessResponse(string sessionToken, double responseRequestCharge, HttpStatusCode? statusCode)
        {
            if (_fhirRequestContextAccessor.FhirRequestContext == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(sessionToken))
            {
                _fhirRequestContextAccessor.FhirRequestContext.ResponseHeaders[CosmosDbHeaders.SessionToken] = sessionToken;
            }

            await AddRequestChargeToFhirRequestContext(responseRequestCharge, statusCode);
        }

        private async Task AddRequestChargeToFhirRequestContext(double responseRequestCharge, HttpStatusCode? statusCode)
        {
            IFhirRequestContext requestContext = _fhirRequestContextAccessor.FhirRequestContext;

            // If there has already been a request to the database for this request, then we want to append a second charge header.
            if (requestContext.ResponseHeaders.TryGetValue(CosmosDbHeaders.RequestCharge, out StringValues existingHeaderValue))
            {
                requestContext.ResponseHeaders[CosmosDbHeaders.RequestCharge] = StringValues.Concat(existingHeaderValue, responseRequestCharge.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                requestContext.ResponseHeaders[CosmosDbHeaders.RequestCharge] = responseRequestCharge.ToString(CultureInfo.InvariantCulture);
            }

            var cosmosMetrics = new CosmosStorageRequestMetricsNotification(requestContext.AuditEventType, requestContext.ResourceType)
            {
                TotalRequestCharge = responseRequestCharge,
            };

            if (statusCode.HasValue && statusCode == HttpStatusCode.TooManyRequests)
            {
                cosmosMetrics.IsThrottled = true;
            }

            try
            {
                await _mediator.Publish(cosmosMetrics, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unable to publish CosmosDB metric.");
            }
        }
    }
}
