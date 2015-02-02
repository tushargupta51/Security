// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.OptionsModel;
using System;
using Microsoft.AspNet.Security;

namespace Microsoft.AspNet.Builder
{
    /// <summary>
    /// Extension methods provided by the claims transformation middleware
    /// </summary>
    public static class ClaimsTransformationExtensions
    {
        public static IServiceCollection ConfigureClaimsTransformation([NotNull] this IServiceCollection services, [NotNull] Action<ClaimsTransformationOptions> configure)
        {
            return services.Configure(configure);
        }

        /// <summary>
        /// Adds a cookie-based authentication middleware to your web application pipeline.
        /// </summary>
        /// <param name="app">The IApplicationBuilder passed to your configuration method</param>
        /// <param name="configureOptions">Used to configure the options for the middleware</param>
        /// <param name="optionsName">The name of the options class that controls the middleware behavior, null will use the default options</param>
        /// <returns>The original app parameter</returns>
        public static IApplicationBuilder UseClaimsTransformation([NotNull] this IApplicationBuilder app, Action<ClaimsTransformationOptions> configureOptions = null)
        {
            return app.UseMiddleware<ClaimsTransformationMiddleware>(configureOptions);
        }
    }
}