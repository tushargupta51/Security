﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNet.Authentication.Notifications;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.Framework.Logging;
using Microsoft.IdentityModel.Protocols;
using Newtonsoft.Json.Linq;

namespace Microsoft.AspNet.Authentication.OpenIdConnect
{
    /// <summary>
    /// A per-request authentication handler for the OpenIdConnectAuthenticationMiddleware.
    /// </summary>
    public class OpenIdConnectAuthenticationHandler : AuthenticationHandler<OpenIdConnectAuthenticationOptions>
    {
        private const string NonceProperty = "N";
        private const string UriSchemeDelimiter = "://";
        private OpenIdConnectConfiguration _configuration;

        private string CurrentUri
        {
            get
            {
                return Request.Scheme +
                       UriSchemeDelimiter +
                       Request.Host +
                       Request.PathBase +
                       Request.Path +
                       Request.QueryString;
            }
        }

        protected HttpClient Backchannel { get; private set; }

        public OpenIdConnectAuthenticationHandler(HttpClient backchannel)
        {
            Backchannel = backchannel;
        }

        protected override void ApplyResponseGrant()
        {
            ApplyResponseGrantAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Handles Signout
        /// </summary>
        /// <returns></returns>
        protected override async Task ApplyResponseGrantAsync()
        {
            var signout = SignOutContext;
            if (signout != null)
            {
                if (_configuration == null && Options.ConfigurationManager != null)
                {
                    _configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
                }

                var message = new OpenIdConnectMessage()
                {
                    IssuerAddress = _configuration == null ? string.Empty : (_configuration.EndSessionEndpoint ?? string.Empty),
                    RequestType = OpenIdConnectRequestType.LogoutRequest,
                };

                // Set End_Session_Endpoint in order:
                // 1. properties.Redirect
                // 2. Options.PostLogoutRedirectUri
                var properties = new AuthenticationProperties(signout.Properties);
                if (!string.IsNullOrEmpty(properties.RedirectUri))
                {
                    message.PostLogoutRedirectUri = properties.RedirectUri;
                }
                else if (!string.IsNullOrWhiteSpace(Options.PostLogoutRedirectUri))
                {
                    message.PostLogoutRedirectUri = Options.PostLogoutRedirectUri;
                }

                var notification = new RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options, message, properties);
                if (Options.Notifications.RedirectToIdentityProvider != null)
                {
                    await Options.Notifications.RedirectToIdentityProvider(notification);
                }

                if (!notification.HandledResponse || !notification.Skipped)
                {
                    var redirectUri = notification.ProtocolMessage.CreateLogoutRequestUrl();
                    if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                    {
                        Logger.LogWarning(Resources.OIDCH_0051_RedirectUriLogoutIsNotWellFormed, redirectUri);
                    }

                    Response.Redirect(redirectUri);
                }
            }
        }

        protected override void ApplyResponseChallenge()
        {
            ApplyResponseChallengeAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Responds to a 401 Challenge. Sends an OpenIdConnect message to the 'identity authority' to obtain an identity.
        /// </summary>
        /// <returns></returns>
        /// <remarks>Uses log id's OIDCH-0026 - OIDCH-0050, next num: 37</remarks>
        protected override async Task ApplyResponseChallengeAsync()
        {
            Logger.LogDebug(Resources.OIDCH_0026_ApplyResponseChallengeAsync, this.GetType());

            if (ShouldConvertChallengeToForbidden())
            {
                Logger.LogDebug(Resources.OIDCH_0027_401_ConvertedTo_403);
                Response.StatusCode = 403;
                return;
            }

            if (Response.StatusCode != 401)
            {
                Logger.LogDebug(Resources.OIDCH_0028_StatusCodeNot401, Response.StatusCode);
                return;
            }

            // When Automatic should redirect on 401 even if there wasn't an explicit challenge.
            if (ChallengeContext == null && !Options.AutomaticAuthentication)
            {
                Logger.LogDebug(Resources.OIDCH_0029_ChallengeContextEqualsNull);
                return;
            }

            // order for local RedirectUri
            // 1. challenge.Properties.RedirectUri
            // 2. CurrentUri if Options.DefaultToCurrentUriOnRedirect is true)
            AuthenticationProperties properties;
            if (ChallengeContext == null)
            {
                properties = new AuthenticationProperties();
            }
            else
            {
                properties = new AuthenticationProperties(ChallengeContext.Properties);
            }

            if (!string.IsNullOrWhiteSpace(properties.RedirectUri))
            {
                Logger.LogDebug(Resources.OIDCH_0030_Using_Properties_RedirectUri, properties.RedirectUri);
            }
            else if (Options.DefaultToCurrentUriOnRedirect)
            {
                Logger.LogDebug(Resources.OIDCH_0032_UsingCurrentUriRedirectUri, CurrentUri);
                properties.RedirectUri = CurrentUri;
            }

            // When redeeming a 'code' for an AccessToken, this value is needed
            if (!string.IsNullOrWhiteSpace(Options.RedirectUri))
            {
                Logger.LogDebug(Resources.OIDCH_0031_Using_Options_RedirectUri, Options.RedirectUri);
                properties.Items.Add(OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey, Options.RedirectUri);
            }

            if (_configuration == null && Options.ConfigurationManager != null)
            {
                _configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
            }

            var message = new OpenIdConnectMessage
            {
                ClientId = Options.ClientId,
                IssuerAddress = _configuration?.AuthorizationEndpoint ?? string.Empty,
                RedirectUri = Options.RedirectUri,
                // [brentschmaltz] - #215 this should be a property on RedirectToIdentityProviderNotification not on the OIDCMessage.
                RequestType = OpenIdConnectRequestType.AuthenticationRequest,
                Resource = Options.Resource,
                ResponseMode = Options.ResponseMode,
                ResponseType = Options.ResponseType,
                Scope = Options.Scope
            };

            if (Options.ProtocolValidator.RequireNonce)
            {
                message.Nonce = Options.ProtocolValidator.GenerateNonce();
            }

            var redirectToIdentityProviderNotification =
                new RedirectToIdentityProviderNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options, message, properties);

            await Options.Notifications.RedirectToIdentityProvider(redirectToIdentityProviderNotification);
            if (redirectToIdentityProviderNotification.HandledResponse)
            {
                Logger.LogInformation(Resources.OIDCH_0034_RedirectToIdentityProviderNotificationHandledResponse);
                return;
            }
            else if (redirectToIdentityProviderNotification.Skipped)
            {
                Logger.LogInformation(Resources.OIDCH_0035_RedirectToIdentityProviderNotificationSkipped);
                return;
            }

            if (!string.IsNullOrWhiteSpace(message.Nonce))
            {
                if (Options.NonceCache != null)
                {
                    if (!Options.NonceCache.TryAddNonce(message.Nonce))
                    {
                        Logger.LogError(Resources.OIDCH_0033_TryAddNonceFailed, message.Nonce);
                        throw new OpenIdConnectProtocolException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0033_TryAddNonceFailed, message.Nonce));
                    }
                }
                else
                {
                    WriteNonceCookie(message.Nonce);
                }
            }

            // If 'OpenIdConnectMessage.State' is null, then the user never set it in the notification. Just set the state
            if (string.IsNullOrWhiteSpace(redirectToIdentityProviderNotification.ProtocolMessage.State))
            {
                redirectToIdentityProviderNotification.ProtocolMessage.State = OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey + "=" + Options.StateDataFormat.Protect(properties);
            }
            // the user did set 'OpenIdConnectMessage.State, add a parameter to the state
            else
            {
                redirectToIdentityProviderNotification.ProtocolMessage.State = OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey + "=" + Options.StateDataFormat.Protect(properties) + "&userstate=" + redirectToIdentityProviderNotification.ProtocolMessage.State;
            }

            var redirectUri = redirectToIdentityProviderNotification.ProtocolMessage.CreateAuthenticationRequestUrl();
            if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
            {
                Logger.LogWarning(Resources.OIDCH_0036_UriIsNotWellFormed, redirectUri);
            }

            Logger.LogDebug(Resources.OIDCH_0037_RedirectUri, redirectUri);

            Response.Redirect(redirectUri);
        }

        protected override AuthenticationTicket AuthenticateCore()
        {
            return AuthenticateCoreAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Invoked to process incoming OpenIdConnect messages.
        /// </summary>
        /// <returns>An <see cref="AuthenticationTicket"/> if successful.</returns>
        /// <remarks>Uses log id's OIDCH-0000 - OIDCH-0025</remarks>
        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            OpenIdConnectMessage tokens = null;
            bool tokensRetrievedUsingCode = false;
            Logger.LogDebug(Resources.OIDCH_0000_AuthenticateCoreAsync, this.GetType());

            // Allow login to be constrained to a specific path. Need to make this runtime configurable.
            if (Options.CallbackPath.HasValue && Options.CallbackPath != (Request.PathBase + Request.Path))
            {
                return null;
            }

            OpenIdConnectMessage message = null;

            // assumption: if the ContentType is "application/x-www-form-urlencoded" it should be safe to read as it is small.
            if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(Request.ContentType)
              // May have media/type; charset=utf-8, allow partial match.
              && Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              && Request.Body.CanRead)
            {
                var form = await Request.ReadFormAsync();
                Request.Body.Seek(0, SeekOrigin.Begin);
                message = new OpenIdConnectMessage(form);
            }

            if (message == null)
            {
                return null;
            }

            try
            {
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(Resources.OIDCH_0001_MessageReceived, message.BuildRedirectUrl());
                }

                var messageReceivedNotification =
                    new MessageReceivedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                    {
                        ProtocolMessage = message
                    };

                await Options.Notifications.MessageReceived(messageReceivedNotification);
                if (messageReceivedNotification.HandledResponse)
                {
                    Logger.LogInformation(Resources.OIDCH_0002_MessageReceivedNotificationHandledResponse);
                    return messageReceivedNotification.AuthenticationTicket;
                }

                if (messageReceivedNotification.Skipped)
                {
                    Logger.LogInformation(Resources.OIDCH_0003_MessageReceivedNotificationSkipped);
                    return null;
                }

                var properties = new AuthenticationProperties();
                
                // if state exists and we failed to 'unprotect' this is not a message we should process.
                if (string.IsNullOrWhiteSpace(message.State))
                {
                    Logger.LogInformation(Resources.OIDCH_0004_MessageStateIsNullOrWhiteSpace);
                }
                else
                {
                    properties = GetPropertiesFromState(message.State);
                    if (properties == null)
                    {
                        Logger.LogError(Resources.OIDCH_0005_MessageStateIsInvalid);
                        return null;
                    }
                }

                // if any of the error fields are set, return null
                if (!string.IsNullOrWhiteSpace(message.Error))
                {
                   Logger.LogError(Resources.OIDCH_0006_MessageContainsError, message.Error, message.ErrorDescription ?? "ErrorDecription null", message.ErrorUri ?? "ErrorUri null");
                   throw new SecurityTokenException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0006_MessageContainsError, message.Error, message.ErrorDescription ?? "ErrorDecription null", message.ErrorUri ?? "ErrorUri null"));
                }

                if (_configuration == null && Options.ConfigurationManager != null)
                {
                    Logger.LogDebug(Resources.OIDCH_0007_UpdatingConfiguration);
                    _configuration = await Options.ConfigurationManager.GetConfigurationAsync(Context.RequestAborted);
                }

                // Redeeming authorization code for tokens
                if (string.IsNullOrWhiteSpace(message.IdToken) && !string.IsNullOrEmpty(message.Code))
                {
                    Logger.LogDebug(Resources.OIDCH_0038_Redeeming_Auth_Code, message.Code);

                    var redirectUri = properties.Items.ContainsKey(OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey) ? 
                       properties.Items[OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey] : Options.RedirectUri;

                    tokens = await RedeemAuthorizationCode(message.Code, redirectUri);
                    if (tokens != null)
                    {
                        message.IdToken = tokens.IdToken;
                        tokensRetrievedUsingCode = true;
                    }
                }
                AuthenticationTicket ticket = null;
                JwtSecurityToken jwt = null;

                // OpenIdConnect protocol allows a Code to be received without the id_token
                if (!string.IsNullOrWhiteSpace(message.IdToken))
                {
                    Logger.LogDebug(Resources.OIDCH_0020_IdTokenReceived, message.IdToken);
                    var securityTokenReceivedNotification =
                        new SecurityTokenReceivedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                        {
                            ProtocolMessage = message
                        };

                    await Options.Notifications.SecurityTokenReceived(securityTokenReceivedNotification);
                    if (securityTokenReceivedNotification.HandledResponse)
                    {
                        Logger.LogInformation(Resources.OIDCH_0008_SecurityTokenReceivedNotificationHandledResponse);
                        return securityTokenReceivedNotification.AuthenticationTicket;
                    }

                    if (securityTokenReceivedNotification.Skipped)
                    {
                        Logger.LogInformation(Resources.OIDCH_0009_SecurityTokenReceivedNotificationSkipped);
                        return null;
                    }

                    // Copy and augment to avoid cross request race conditions for updated configurations.
                    var validationParameters = Options.TokenValidationParameters.Clone();
                    if (_configuration != null)
                    {
                        if (string.IsNullOrWhiteSpace(validationParameters.ValidIssuer))
                        {
                            validationParameters.ValidIssuer = _configuration.Issuer;
                        }
                        else if (!string.IsNullOrWhiteSpace(_configuration.Issuer))
                        {
                            validationParameters.ValidIssuers = validationParameters.ValidIssuers?.Concat(new[] { _configuration.Issuer }) ?? new[] { _configuration.Issuer };
                        }

                        validationParameters.IssuerSigningKeys = validationParameters.IssuerSigningKeys?.Concat(_configuration.SigningKeys) ?? _configuration.SigningKeys;
                    }

                    SecurityToken validatedToken = null;
                    ClaimsPrincipal principal = null;
                    foreach (var validator in Options.SecurityTokenValidators)
                    {
                        if (validator.CanReadToken(message.IdToken))
                        {
                            principal = validator.ValidateToken(message.IdToken, validationParameters, out validatedToken);
                            jwt = validatedToken as JwtSecurityToken;
                            if (jwt == null)
                            {
                                Logger.LogError(Resources.OIDCH_0010_ValidatedSecurityTokenNotJwt, validatedToken?.GetType());
                                throw new SecurityTokenException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0010_ValidatedSecurityTokenNotJwt, validatedToken?.GetType()));
                            }
                        }
                    }

                    if (validatedToken == null)
                    {
                        Logger.LogDebug(Resources.OIDCH_0011_UnableToValidateToken, message.IdToken);
                        throw new SecurityTokenException(string.Format(CultureInfo.InvariantCulture, Resources.OIDCH_0011_UnableToValidateToken, message.IdToken));
                    }

                    ticket = new AuthenticationTicket(principal, properties, Options.AuthenticationScheme);
                    if (!string.IsNullOrWhiteSpace(message.SessionState))
                    {
                        ticket.Properties.Items[OpenIdConnectSessionProperties.SessionState] = message.SessionState;
                    }

                    if (_configuration != null && !string.IsNullOrWhiteSpace(_configuration.CheckSessionIframe))
                    {
                        ticket.Properties.Items[OpenIdConnectSessionProperties.CheckSessionIFrame] = _configuration.CheckSessionIframe;
                    }

                    // Rename?
                    if (Options.UseTokenLifetime)
                    {
                        var issued = validatedToken.ValidFrom;
                        if (issued != DateTime.MinValue)
                        {
                            ticket.Properties.IssuedUtc = issued;
                        }

                        var expires = validatedToken.ValidTo;
                        if (expires != DateTime.MinValue)
                        {
                            ticket.Properties.ExpiresUtc = expires;
                        }
                    }

                    if (Options.GetClaimsFromUserInfoEndpoint)
                    {
                        Logger.LogDebug(Resources.OIDCH_0040_Sending_Request_UIEndpoint);
                        ticket = await GetUserInformationAsync(properties, tokens, ticket);
                    }

                    var securityTokenValidatedNotification =
                        new SecurityTokenValidatedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                        {
                            AuthenticationTicket = ticket,
                            ProtocolMessage = message
                        };

                    await Options.Notifications.SecurityTokenValidated(securityTokenValidatedNotification);
                    if (securityTokenValidatedNotification.HandledResponse)
                    {
                        Logger.LogInformation(Resources.OIDCH_0012_SecurityTokenValidatedNotificationHandledResponse);
                        return securityTokenValidatedNotification.AuthenticationTicket;
                    }

                    if (securityTokenValidatedNotification.Skipped)
                    {
                        Logger.LogInformation(Resources.OIDCH_0013_SecurityTokenValidatedNotificationSkipped);
                        return null;
                    }

                    string nonce = jwt.Payload.Nonce;
                    if (Options.NonceCache != null)
                    {
                        // if the nonce cannot be removed, it was used
                        if (!Options.NonceCache.TryRemoveNonce(nonce))
                        {
                            nonce = null;
                        }
                    }
                    else
                    {
                        nonce = ReadNonceCookie(nonce);
                    }

                    var protocolValidationContext = new OpenIdConnectProtocolValidationContext
                    {
                        AuthorizationCode = message.Code,
                        Nonce = nonce,
                    };

                    // If id_token is received using code only flow, no need to validate chash. Setting authroization code to null will skip chash validation.
                    if (tokensRetrievedUsingCode)
                    {
                        protocolValidationContext.AuthorizationCode = null;
                    }

                    Options.ProtocolValidator.Validate(jwt, protocolValidationContext);
                }

                if (message.Code != null)
                {
                    Logger.LogDebug(Resources.OIDCH_0014_AuthorizationCodeReceived, message.Code);
                    if (ticket == null)
                    {
                        ticket = new AuthenticationTicket(properties, Options.AuthenticationScheme);
                    }

                    var authorizationCodeReceivedNotification = new AuthorizationCodeReceivedNotification(Context, Options)
                    {
                        AuthenticationTicket = ticket,
                        Code = message.Code,
                        JwtSecurityToken = jwt,
                        ProtocolMessage = message,
                        RedirectUri = ticket.Properties.Items.ContainsKey(OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey) ?
                                      ticket.Properties.Items[OpenIdConnectAuthenticationDefaults.RedirectUriUsedForCodeKey] : string.Empty,
                    };

                    await Options.Notifications.AuthorizationCodeReceived(authorizationCodeReceivedNotification);
                    if (authorizationCodeReceivedNotification.HandledResponse)
                    {
                        Logger.LogInformation(Resources.OIDCH_0015_AuthorizationCodeReceivedNotificationHandledResponse);
                        return authorizationCodeReceivedNotification.AuthenticationTicket;
                    }

                    if (authorizationCodeReceivedNotification.Skipped)
                    {
                        Logger.LogInformation(Resources.OIDCH_0016_AuthorizationCodeReceivedNotificationSkipped);
                        return null;
                    }
                }

                return ticket;
            }
            catch (Exception exception)
            {
                Logger.LogError(Resources.OIDCH_0017_ExceptionOccurredWhileProcessingMessage, exception);

                // Refresh the configuration for exceptions that may be caused by key rollovers. The user can also request a refresh in the notification.
                if (Options.RefreshOnIssuerKeyNotFound && exception.GetType().Equals(typeof(SecurityTokenSignatureKeyNotFoundException)))
                {
                    if (Options.ConfigurationManager != null)
                    {
                        Logger.LogDebug(Resources.OIDCH_0021_AutomaticConfigurationRefresh);
                        Options.ConfigurationManager.RequestRefresh();
                    }
                }

                var authenticationFailedNotification =
                    new AuthenticationFailedNotification<OpenIdConnectMessage, OpenIdConnectAuthenticationOptions>(Context, Options)
                    {
                        ProtocolMessage = message,
                        Exception = exception
                    };

                await Options.Notifications.AuthenticationFailed(authenticationFailedNotification);
                if (authenticationFailedNotification.HandledResponse)
                {
                    Logger.LogInformation(Resources.OIDCH_0018_AuthenticationFailedNotificationHandledResponse);
                    return authenticationFailedNotification.AuthenticationTicket;
                }

                if (authenticationFailedNotification.Skipped)
                {
                    Logger.LogInformation(Resources.OIDCH_0019_AuthenticationFailedNotificationSkipped);
                    return null;
                }

                throw;
            }
        }

        protected virtual async Task<OpenIdConnectMessage> RedeemAuthorizationCode(string authorizationCode, string redirectUri)
        {
            var openIdMessage = new OpenIdConnectMessage()
            {
                ClientId = Options.ClientId,
                ClientSecret = Options.ClientSecret,
                Code = authorizationCode,
                GrantType = "authorization_code",
                RedirectUri = redirectUri
            };

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, _configuration.TokenEndpoint);
            requestMessage.Content = new FormUrlEncodedContent(openIdMessage.Parameters);
            var responseMessage = await Backchannel.SendAsync(requestMessage);
            responseMessage.EnsureSuccessStatusCode();
            var tokenResonse = await responseMessage.Content.ReadAsStringAsync();
            var jsonTokenResponse = JObject.Parse(tokenResonse);
            return new OpenIdConnectMessage()
            {
                AccessToken = jsonTokenResponse.Value<string>(OpenIdConnectParameterNames.AccessToken),
                IdToken = jsonTokenResponse.Value<string>(OpenIdConnectParameterNames.IdToken),
                TokenType = jsonTokenResponse.Value<string>(OpenIdConnectParameterNames.TokenType),
                ExpiresIn = jsonTokenResponse.Value<string>(OpenIdConnectParameterNames.ExpiresIn)
            };
        }

        protected virtual async Task<AuthenticationTicket> GetUserInformationAsync(AuthenticationProperties properties, OpenIdConnectMessage message, AuthenticationTicket ticket)
        {
            string userInfoEndpoint = null;
            if (_configuration != null)
            {
                userInfoEndpoint = _configuration.UserInfoEndpoint;
            }

            if (string.IsNullOrEmpty(userInfoEndpoint))
            {
                userInfoEndpoint = Options.Configuration != null ? Options.Configuration.UserInfoEndpoint : null;
            }

            if (string.IsNullOrEmpty(userInfoEndpoint))
            {
                Logger.LogError("UserInfo endpoint is not set. Request to retrieve claims from userinfo endpoint cannot be completed.");
                return ticket;
            }

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", message.AccessToken);
            var responseMessage = await Backchannel.SendAsync(requestMessage);
            responseMessage.EnsureSuccessStatusCode();
            var userInfoResponse = await responseMessage.Content.ReadAsStringAsync();
            var user = JObject.Parse(userInfoResponse);

            var identity = (ClaimsIdentity)ticket.Principal.Identity;
            var subject = identity.FindFirst(ClaimTypes.NameIdentifier).Value;

            // check if the sub claim matches
            var userInfoSubject = user.Value<string>("sub");
            if (userInfoSubject == null || !string.Equals(userInfoSubject, subject, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogError(Resources.OIDCH_0039_Subject_Claim_Mismatch);
                throw new ArgumentException(Resources.OIDCH_0039_Subject_Claim_Mismatch); 
            }

            var userInfoIdentity = new ClaimsIdentity(identity);
            foreach (var pair in user)
            {
                JToken value;
                var claimValue = user.TryGetValue(pair.Key, out value) ? value.ToString() : null;

                var inboundClaimTypeMap = new Dictionary<string, string>(JwtSecurityTokenHandler.InboundClaimTypeMap);
                string longClaimTypeName;
                inboundClaimTypeMap.TryGetValue(pair.Key, out longClaimTypeName);

                // checking if claim exist with either short or long name
                if (!(identity.HasClaim(pair.Key, claimValue) || identity.HasClaim(longClaimTypeName, claimValue)))
                {
                    userInfoIdentity.AddClaim(new Claim(pair.Key, claimValue, ClaimValueTypes.String, Options.ClaimsIssuer));
                }
            }

            ticket.Principal.AddIdentity(userInfoIdentity);
            return ticket;
        }

        /// <summary>
        /// Adds the nonce to <see cref="HttpResponse.Cookies"/>.
        /// </summary>
        /// <param name="nonce">the nonce to remember.</param>
        /// <remarks><see cref="HttpResponse.Cookies.Append"/>is called to add a cookie with the name: 'OpenIdConnectAuthenticationDefaults.Nonce + <see cref="OpenIdConnectAuthenticationOptions.StringDataFormat.Protect"/>(nonce)'.
        /// The value of the cookie is: "N".</remarks>
        private void WriteNonceCookie(string nonce)
        {
            if (string.IsNullOrEmpty(nonce))
            {
                throw new ArgumentNullException(nameof(nonce));
            }

            Response.Cookies.Append(
                OpenIdConnectAuthenticationDefaults.CookieNoncePrefix + Options.StringDataFormat.Protect(nonce),
                NonceProperty,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps
                });
        }

        /// <summary>
        /// Searches <see cref="HttpRequest.Cookies"/> for a matching nonce.
        /// </summary>
        /// <param name="nonce">the nonce that we are looking for.</param>
        /// <returns>echos 'nonce' if a cookie is found that matches, null otherwise.</returns>
        /// <remarks>Examine <see cref="HttpRequest.Cookies.Keys"/> that start with the prefix: 'OpenIdConnectAuthenticationDefaults.Nonce'. 
        /// <see cref="OpenIdConnectAuthenticationOptions.StringDataFormat.Unprotect"/> is used to obtain the actual 'nonce'. If the nonce is found, then <see cref="HttpResponse.Cookies.Delete"/> is called.</remarks>
        private string ReadNonceCookie(string nonce)
        {
            if (nonce == null)
            {
                return null;
            }

            foreach (var nonceKey in Request.Cookies.Keys)
            {
                if (nonceKey.StartsWith(OpenIdConnectAuthenticationDefaults.CookieNoncePrefix))
                {
                    try
                    {
                        var nonceDecodedValue = Options.StringDataFormat.Unprotect(nonceKey.Substring(OpenIdConnectAuthenticationDefaults.CookieNoncePrefix.Length, nonceKey.Length - OpenIdConnectAuthenticationDefaults.CookieNoncePrefix.Length));
                        if (nonceDecodedValue == nonce)
                        {
                            var cookieOptions = new CookieOptions
                            {
                                HttpOnly = true,
                                Secure = Request.IsHttps
                            };

                            Response.Cookies.Delete(nonceKey, cookieOptions);
                            return nonce;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Failed to un-protect the nonce cookie.", ex);
                    }
                }
            }

            return null;
        }

        private AuthenticationProperties GetPropertiesFromState(string state)
        {
            // assume a well formed query string: <a=b&>OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey=kasjd;fljasldkjflksdj<&c=d>
            var startIndex = 0;
            if (string.IsNullOrWhiteSpace(state) || (startIndex = state.IndexOf(OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey, StringComparison.Ordinal)) == -1)
            {
                return null;
            }

            var authenticationIndex = startIndex + OpenIdConnectAuthenticationDefaults.AuthenticationPropertiesKey.Length;
            if (authenticationIndex == -1 || authenticationIndex == state.Length || state[authenticationIndex] != '=')
            {
                return null;
            }

            // scan rest of string looking for '&'
            authenticationIndex++;
            var endIndex = state.Substring(authenticationIndex, state.Length - authenticationIndex).IndexOf("&", StringComparison.Ordinal);

            // -1 => no other parameters are after the AuthenticationPropertiesKey
            if (endIndex == -1)
            {
                return Options.StateDataFormat.Unprotect(Uri.UnescapeDataString(state.Substring(authenticationIndex).Replace('+', ' ')));
            }
            else
            {
                return Options.StateDataFormat.Unprotect(Uri.UnescapeDataString(state.Substring(authenticationIndex, endIndex).Replace('+', ' ')));
            }
        }

        /// <summary>
        /// Calls InvokeReplyPathAsync
        /// </summary>
        /// <returns>True if the request was handled, false if the next middleware should be invoked.</returns>
        public override Task<bool> InvokeAsync()
        {
            return InvokeReplyPathAsync();
        }

        private async Task<bool> InvokeReplyPathAsync()
        {
            var ticket = await AuthenticateAsync();
            if (ticket != null)
            {
                if (ticket.Principal != null)
                {
                    Request.HttpContext.Authentication.SignIn(Options.SignInScheme, ticket.Principal, ticket.Properties);
                }

                // Redirect back to the original secured resource, if any.
                if (!string.IsNullOrWhiteSpace(ticket.Properties.RedirectUri))
                {
                    Response.Redirect(ticket.Properties.RedirectUri);
                    return true;
                }
            }

            return false;
        }
    }
}
