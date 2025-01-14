﻿//
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//
using Microsoft.Azure.WebJobs;
using Microsoft.Online.SharePoint.TenantAdministration;
using Microsoft.SharePoint.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using OfficeDevPnP.Core;
using OfficeDevPnP.Core.Framework.Provisioning.Connectors;
using OfficeDevPnP.Core.Framework.Provisioning.Model;
using OfficeDevPnP.Core.Framework.Provisioning.ObjectHandlers;
using OfficeDevPnP.Core.Framework.Provisioning.Providers;
using OfficeDevPnP.Core.Framework.Provisioning.Providers.Xml;
using OfficeDevPnP.Core.Utilities.Themes;
using SharePointPnP.ProvisioningApp.DomainModel;
using SharePointPnP.ProvisioningApp.Infrastructure;
using SharePointPnP.ProvisioningApp.Infrastructure.DomainModel.Provisioning;
using SharePointPnP.ProvisioningApp.Infrastructure.Mail;
using SharePointPnP.ProvisioningApp.Infrastructure.Telemetry;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SharePointPnP.ProvisioningApp.WebJob
{
    public static class ProvisioningFunction
    {
        private static KnownExceptions knownExceptions;

        static ProvisioningFunction()
        {
            // Get the JSON settings for known exceptions
            Stream stream = typeof(KnownExceptions)
                .Assembly
                .GetManifestResourceStream("SharePointPnP.ProvisioningApp.WebJob.known-exceptions.json");

            // If we have the stream and it can be read
            if (stream != null && stream.CanRead)
            {
                using (var sr = new StreamReader(stream))
                {
                    // Deserialize it
                    knownExceptions = JsonConvert.DeserializeObject<KnownExceptions>(sr.ReadToEnd());
                }
            }
        }

        public static async Task RunAsync([QueueTrigger("actions")]ProvisioningActionModel action, TextWriter log)
        {
            var startProvisioning = DateTime.Now;

            String provisioningEnvironment = ConfigurationManager.AppSettings["SPPA:ProvisioningEnvironment"];

            log.WriteLine($"Processing queue trigger function for {action.UserPrincipalName} on tenant {action.TenantId}");
            log.WriteLine($"PnP Correlation ID: {action.CorrelationId.ToString()}");

            // Instantiate and use the telemetry model
            TelemetryUtility telemetry = new TelemetryUtility(log);
            Dictionary<string, string> telemetryProperties = new Dictionary<string, string>();

            // Configure telemetry properties
            // telemetryProperties.Add("UserPrincipalName", action.UserPrincipalName);
            // telemetryProperties.Add("TenantId", action.TenantId);
            telemetryProperties.Add("PnPCorrelationId", action.CorrelationId.ToString());
            telemetryProperties.Add("TargetSiteAlreadyExists", action.TargetSiteAlreadyExists.ToString());
            telemetryProperties.Add("TargetSiteBaseTemplateId", action.TargetSiteBaseTemplateId);

            var appOnlyAccessToken = await ProvisioningAppManager.AccessTokenProvider.GetAppOnlyAccessTokenAsync(
                "https://graph.microsoft.com/",
                ConfigurationManager.AppSettings["OfficeDevPnP:TenantId"],
                ConfigurationManager.AppSettings["OfficeDevPnP:ClientId"],
                ConfigurationManager.AppSettings["OfficeDevPnP:ClientSecret"],
                ConfigurationManager.AppSettings["OfficeDevPnP:AppUrl"]);

            // Get a reference to the data context
            ProvisioningAppDBContext dbContext = new ProvisioningAppDBContext();

            try
            {
                // Log telemetry event
                telemetry?.LogEvent("ProvisioningFunction.Start");

                var tokenId = $"{action.TenantId}-{action.UserPrincipalName.GetHashCode()}-{action.ActionType.ToString().ToLower()}-{provisioningEnvironment}";

                // Retrieve the SPO target tenant via Microsoft Graph
                var graphAccessToken = await ProvisioningAppManager.AccessTokenProvider.GetAccessTokenAsync(
                    tokenId, "https://graph.microsoft.com/",
                    ConfigurationManager.AppSettings[$"{action.ActionType}:ClientId"],
                    ConfigurationManager.AppSettings[$"{action.ActionType}:ClientSecret"],
                    ConfigurationManager.AppSettings[$"{action.ActionType}:AppUrl"]);

                if (!String.IsNullOrEmpty(graphAccessToken))
                {
                    #region Get current context data (User, SPO Tenant, SPO Access Token)

                    // Get the currently connected user name and email (UPN)
                    var jwtAccessToken = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(graphAccessToken);

                    String delegatedUPN = String.Empty;
                    var upnClaim = jwtAccessToken.Claims.FirstOrDefault(c => c.Type == "upn");
                    if (upnClaim != null && !String.IsNullOrEmpty(upnClaim.Value))
                    {
                        delegatedUPN = upnClaim.Value;
                    }

                    String delegatedUserName = String.Empty;
                    var nameClaim = jwtAccessToken.Claims.FirstOrDefault(c => c.Type == "name");
                    if (nameClaim != null && !String.IsNullOrEmpty(nameClaim.Value))
                    {
                        delegatedUserName = nameClaim.Value;
                    }

                    // Determine the URL of the root SPO site for the current tenant
                    var rootSiteJson = HttpHelper.MakeGetRequestForString("https://graph.microsoft.com/v1.0/sites/root", graphAccessToken);
                    SharePointSite rootSite = JsonConvert.DeserializeObject<SharePointSite>(rootSiteJson);

                    String spoTenant = rootSite.WebUrl;

                    log.WriteLine($"Target SharePoint Online Tenant: {spoTenant}");

                    // Configure telemetry properties
                    telemetryProperties.Add("SPOTenant", spoTenant);

                    // Retrieve the SPO Access Token
                    var spoAccessToken = await ProvisioningAppManager.AccessTokenProvider.GetAccessTokenAsync(
                        tokenId, rootSite.WebUrl,
                        ConfigurationManager.AppSettings[$"{action.ActionType}:ClientId"],
                        ConfigurationManager.AppSettings[$"{action.ActionType}:ClientSecret"],
                        ConfigurationManager.AppSettings[$"{action.ActionType}:AppUrl"]);
                    log.WriteLine($"Retrieved target SharePoint Online Access Token.");

                    #endregion

                    // Connect to SPO, create and provision site
                    AuthenticationManager authManager = new AuthenticationManager();
                    using (ClientContext context = authManager.GetAzureADAccessTokenAuthenticatedContext(spoTenant, spoAccessToken))
                    {
                        // Telemetry and startup
                        var web = context.Web;
                        context.ClientTag = $"SPDev:ProvisioningPortal-{provisioningEnvironment}";
                        context.Load(web, w => w.Title, w => w.Id);
                        await context.ExecuteQueryAsync();

                        log.WriteLine($"SharePoint Online Root Site Collection title: {web.Title}");

                        #region Store the main site URL in KeyVault

                        // Store the main site URL in KeyVault
                        var vault = new KeyVaultService();

                        // Read any existing properties for the current tenantId
                        var properties = await vault.GetAsync(tokenId);

                        if (properties == null)
                        {
                            // If there are no properties, create a new dictionary
                            properties = new Dictionary<String, String>();
                        }

                        // Set/Update the RefreshToken value
                        properties["SPORootSite"] = spoTenant;

                        // Add or Update the Key Vault accordingly
                        await vault.AddOrUpdateAsync(tokenId, properties);

                        #endregion

                        #region Provision the package

                        var package = dbContext.Packages.FirstOrDefault(p => p.Id == new Guid(action.PackageId));

                        if (package != null)
                        {
                            // Update the Popularity of the package
                            package.TimesApplied++;
                            dbContext.SaveChanges();

                            #region Get the Provisioning Hierarchy file

                            // Determine reference path variables
                            var blobConnectionString = ConfigurationManager.AppSettings["BlobTemplatesProvider:ConnectionString"];
                            var blobContainerName = ConfigurationManager.AppSettings["BlobTemplatesProvider:ContainerName"];

                            var packageFileName = package.PackageUrl.Substring(package.PackageUrl.LastIndexOf('/') + 1);
                            var packageFileUri = new Uri(package.PackageUrl);
                            var packageFileRelativePath = packageFileUri.AbsolutePath.Substring(2 + blobContainerName.Length);
                            var packageFileRelativeFolder = packageFileRelativePath.Substring(0, packageFileRelativePath.LastIndexOf('/'));

                            // Configure telemetry properties
                            telemetryProperties.Add("PackageFileName", packageFileName);
                            telemetryProperties.Add("PackageFileUri", packageFileUri.ToString());

                            // Read the main provisioning file from the Blob Storage
                            CloudStorageAccount csa;
                            if (!CloudStorageAccount.TryParse(blobConnectionString, out csa))
                                throw new ArgumentException("Cannot create cloud storage account from given connection string.");

                            CloudBlobClient blobClient = csa.CreateCloudBlobClient();
                            CloudBlobContainer blobContainer = blobClient.GetContainerReference(blobContainerName);

                            var blockBlob = blobContainer.GetBlockBlobReference(packageFileRelativePath);

                            // Crate an in-memory copy of the source stream
                            MemoryStream mem = new MemoryStream();
                            await blockBlob.DownloadToStreamAsync(mem);
                            mem.Position = 0;

                            // Prepare the output hierarchy
                            ProvisioningHierarchy hierarchy = null;

                            if (packageFileName.EndsWith(".xml", StringComparison.InvariantCultureIgnoreCase))
                            {
                                // That's an XML Provisioning Template file

                                XDocument xml = XDocument.Load(mem);
                                mem.Position = 0;

                                // Deserialize the stream into a provisioning hierarchy reading any 
                                // dependecy with the Azure Blob Storage connector
                                var formatter = XMLPnPSchemaFormatter.GetSpecificFormatter(xml.Root.Name.NamespaceName);
                                var templateLocalFolder = $"{blobContainerName}/{packageFileRelativeFolder}";

                                var provider = new XMLAzureStorageTemplateProvider(
                                    blobConnectionString,
                                    templateLocalFolder);
                                formatter.Initialize(provider);

                                // Get the full hierarchy
                                hierarchy = ((IProvisioningHierarchyFormatter)formatter).ToProvisioningHierarchy(mem);
                                hierarchy.Connector = provider.Connector;
                            }
                            else if (packageFileName.EndsWith(".pnp", StringComparison.InvariantCultureIgnoreCase))
                            {
                                // That's a PnP Package file

                                // Get a provider based on the in-memory .PNP Open XML file
                                OpenXMLConnector openXmlConnector = new OpenXMLConnector(mem);
                                XMLTemplateProvider provider = new XMLOpenXMLTemplateProvider(
                                    openXmlConnector);

                                // Get the .xml provisioning template file name
                                var xmlTemplateFileName = openXmlConnector.Info?.Properties?.TemplateFileName ??
                                    packageFileName.Substring(packageFileName.LastIndexOf('/') + 1)
                                    .ToLower().Replace(".pnp", ".xml");

                                // Get the full hierarchy
                                hierarchy = provider.GetHierarchy(xmlTemplateFileName);
                                hierarchy.Connector = provider.Connector;
                            }

                            #endregion

                            #region Apply the template

                            // Prepare variable to collect provisioned sites
                            var provisionedSites = new List<Tuple<String, String>>();

                            // If we have a hierarchy with at least one Sequence
                            if (hierarchy != null && hierarchy.Sequences != null && hierarchy.Sequences.Count > 0)
                            {
                                Console.WriteLine($"Provisioning hierarchy \"{hierarchy.DisplayName}\"");

                                var tenantUrl = UrlUtilities.GetTenantAdministrationUrl(context.Url);

                                // Retrieve the SPO Access Token
                                var spoAdminAccessToken = await ProvisioningAppManager.AccessTokenProvider.GetAccessTokenAsync(
                                    tokenId, tenantUrl,
                                    ConfigurationManager.AppSettings[$"{action.ActionType}:ClientId"],
                                    ConfigurationManager.AppSettings[$"{action.ActionType}:ClientSecret"],
                                    ConfigurationManager.AppSettings[$"{action.ActionType}:AppUrl"]);
                                log.WriteLine($"Retrieved target SharePoint Online Admin Center Access Token.");

                                using (var tenantContext = authManager.GetAzureADAccessTokenAuthenticatedContext(tenantUrl, spoAdminAccessToken))
                                {
                                    using (var pnpTenantContext = PnPClientContext.ConvertFrom(tenantContext))
                                    {
                                        var tenant = new Microsoft.Online.SharePoint.TenantAdministration.Tenant(pnpTenantContext);

                                        // Prepare a dictionary to hold the access tokens
                                        var accessTokens = new Dictionary<String, String>();

                                        // Prepare logging for hierarchy application
                                        var ptai = new ProvisioningTemplateApplyingInformation();
                                        ptai.MessagesDelegate += delegate (string message, ProvisioningMessageType messageType) {
                                            log.WriteLine($"{messageType} - {message}");
                                        };
                                        ptai.ProgressDelegate += delegate (string message, int step, int total) {
                                            log.WriteLine($"{step:00}/{total:00} - {message}");
                                        };
                                        ptai.SiteProvisionedDelegate += delegate (string title, string url)
                                        {
                                            log.WriteLine($"Fully provisioned site '{title}' with URL: {url}");
                                            var provisionedSite = new Tuple<string, string>(title, url);
                                            if (!provisionedSites.Contains(provisionedSite))
                                            {
                                                provisionedSites.Add(provisionedSite);
                                            }
                                        };

                                        // Configure the OAuth Access Tokens for the client context
                                        accessTokens.Add(new Uri(tenantUrl).Authority, spoAdminAccessToken);
                                        accessTokens.Add(new Uri(spoTenant).Authority, spoAccessToken);

                                        // Configure the OAuth Access Tokens for the PnPClientContext, too
                                        pnpTenantContext.PropertyBag["AccessTokens"] = accessTokens;
                                        ptai.AccessTokens = accessTokens;

                                        #region Theme handling

                                        // Process the graphical Theme
                                        if (action.ApplyTheme)
                                        {
                                            // If we don't have any custom Theme
                                            if (!action.ApplyCustomTheme)
                                            {
                                                // Associate the selected already existing Theme to all the sites of the hierarchy
                                                foreach (var sc in hierarchy.Sequences[0].SiteCollections)
                                                {
                                                    sc.Theme = action.SelectedTheme;
                                                    foreach (var s in sc.Sites)
                                                    {
                                                        UpdateChildrenSitesTheme(s, action.SelectedTheme);
                                                    }
                                                }
                                            }
                                        }

                                        #endregion

                                        // Configure provisioning parameters
                                        if (action.PackageProperties != null)
                                        {
                                            foreach (var key in action.PackageProperties.Keys)
                                            {
                                                if (hierarchy.Parameters.ContainsKey(key.ToString()))
                                                {
                                                    hierarchy.Parameters[key.ToString()] = action.PackageProperties[key].ToString();
                                                }
                                                else
                                                {
                                                    hierarchy.Parameters.Add(key.ToString(), action.PackageProperties[key].ToString());
                                                }

                                                // Configure telemetry properties
                                                telemetryProperties.Add($"PackageProperty.{key}", action.PackageProperties[key].ToString());
                                            }
                                        }

                                        // Log telemetry event
                                        telemetry?.LogEvent("ProvisioningFunction.BeginProvisioning", telemetryProperties);

                                        // Define a PnPProvisioningContext scope to share the security context across calls
                                        using (var pnpProvisioningContext = new PnPProvisioningContext(async (r, s) =>
                                        {
                                            if (accessTokens.ContainsKey(r))
                                            {
                                                // In this scenario we just use the dictionary of access tokens
                                                // in fact the overall operation for sure will take less than 1 hour
                                                return await Task.FromResult(accessTokens[r]);
                                            }
                                            else
                                            {
                                                // Try to get a fresh new Access Token
                                                var token = await ProvisioningAppManager.AccessTokenProvider.GetAccessTokenAsync(
                                                    tokenId, $"https://{r}",
                                                    ConfigurationManager.AppSettings[$"{action.ActionType}:ClientId"],
                                                    ConfigurationManager.AppSettings[$"{action.ActionType}:ClientSecret"],
                                                    ConfigurationManager.AppSettings[$"{action.ActionType}:AppUrl"]);

                                                accessTokens.Add(r, token);

                                                return (token);
                                            }
                                        }))
                                        {
                                            // Apply the hierarchy
                                            log.WriteLine($"Hierarchy Provisioning Started: {DateTime.Now:hh.mm.ss}");
                                            tenant.ApplyProvisionHierarchy(hierarchy, hierarchy.Sequences[0].ID, ptai);
                                            log.WriteLine($"Hierarchy Provisioning Completed: {DateTime.Now:hh.mm.ss}");
                                        }

                                        if (action.ApplyTheme && action.ApplyCustomTheme)
                                        {
                                            if (!String.IsNullOrEmpty(action.ThemePrimaryColor) &&
                                                !String.IsNullOrEmpty(action.ThemeBodyTextColor) &&
                                                !String.IsNullOrEmpty(action.ThemeBodyBackgroundColor))
                                            {
                                                log.WriteLine($"Applying custom Theme to provisioned sites");

                                                #region Palette generation for Theme

                                                var jsonPalette = ThemeUtility.GetThemeAsJSON(
                                                    action.ThemePrimaryColor,
                                                    action.ThemeBodyTextColor,
                                                    action.ThemeBodyBackgroundColor);

                                                #endregion

                                                // Apply the custom theme to all of the provisioned sites
                                                foreach (var ps in provisionedSites)
                                                {
                                                    using (var provisionedSiteContext = authManager.GetAzureADAccessTokenAuthenticatedContext(ps.Item2, spoAccessToken))
                                                    {
                                                        if (provisionedSiteContext.Web.ApplyTheme(jsonPalette))
                                                        {
                                                            log.WriteLine($"Custom Theme applied on site '{ps.Item1}' with URL: {ps.Item2}");
                                                        }
                                                        else
                                                        {
                                                            log.WriteLine($"Failed to apply custom Theme on site '{ps.Item1}' with URL: {ps.Item2}");
                                                        }
                                                    }
                                                }
                                            }
                                        }

                                        // Log telemetry event
                                        telemetry?.LogEvent("ProvisioningFunction.EndProvisioning", telemetryProperties);

                                        // Notify user about the provisioning outcome
                                        MailHandler.SendMailNotification(
                                            "ProvisioningCompleted",
                                            action.NotificationEmail,
                                            null,
                                            new
                                            {
                                                TemplateName = action.DisplayName,
                                                ProvisionedSites = provisionedSites,
                                            },
                                            appOnlyAccessToken);

                                        // Log reporting event (1 = Success)
                                        logReporting(action, provisioningEnvironment, startProvisioning, package, 1);
                                    }
                                }
                            }
                            else
                            {
                                throw new ApplicationException($"The requested package does not contain a valid PnP Hierarchy!");
                            }

                            #endregion
                        }
                        else
                        {
                            throw new ApplicationException($"Cannot find the package with ID: {action.PackageId}");
                        }

                        #endregion

                        log.WriteLine($"Function successfully executed!");
                        // Log telemetry event
                        telemetry?.LogEvent("ProvisioningFunction.End", telemetryProperties);
                    }
                }
                else
                {
                    var noTokensErrorMessage = $"Cannot retrieve Refresh Token or Access Token for {action.UserPrincipalName} in tenant {action.TenantId}!";
                    log.WriteLine(noTokensErrorMessage);
                    throw new ApplicationException(noTokensErrorMessage);
                }
            }
            catch (Exception ex)
            {
                // Log telemetry event
                telemetry?.LogException(ex, "ProvisioningFunction.RunAsync", telemetryProperties);

                // Notify user about the provisioning outcome
                MailHandler.SendMailNotification(
                    "ProvisioningFailed",
                    action.NotificationEmail,
                    null,
                    new {
                        TemplateName = action.DisplayName,
                        ExceptionDetails = SimplifyException(ex),
                        PnPCorrelationId = action.CorrelationId.ToString(),
                    },
                    appOnlyAccessToken);

                // Log reporting event (2 = Failed)
                logReporting(action, provisioningEnvironment, startProvisioning, null, 2, ex.ToDetailedString());

                throw ex;
            }
            finally
            {
                telemetry?.Flush();
            }
        }

        private static String SimplifyException(Exception ex)
        {
            var knownException = knownExceptions?.Exceptions?.Find(e => ex.GetType().FullName == e.ExceptionType && ex.StackTrace.Contains(e.MatchingText));

            if (knownException != null)
            {
                var tmp = typeof(FriendlyErrorMessages).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

                return (typeof(FriendlyErrorMessages).GetProperty(knownException.FriendlyMessageResourceKey,
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null, null) as String);
            }
            else
            {
                return (ex.ToDetailedString());
            }
        }

        private static void logReporting(ProvisioningActionModel action, string provisioningEnvironment, DateTime startProvisioning, DomainModel.Package package, Int32 outcome, String details = null)
        {
            // Prepare the reporting event
            var provisioningEvent = new
            {
                EventId = action.CorrelationId,
                EventStartDateTime = startProvisioning,
                EventEndDateTime = DateTime.Now,
                EventOutcome = outcome,
                EventDetails = details,
                EventFromProduction = provisioningEnvironment.ToUpper() != "TEST" ? 1 : 0,
                TemplateId = action.PackageId,
                TemplateDisplayName = package?.DisplayName,
            };

            try
            {
                // Make the Azure Function call for reporting
                HttpHelper.MakePostRequest(ConfigurationManager.AppSettings["SPPA:ReportingFunctionUrl"],
                    provisioningEvent, "application/json", null);
            }
            catch
            {
                // Intentionally ignore any reporting issue
            }
        }

        // Method to recursively update the Theme of all children sites
        private static void UpdateChildrenSitesTheme(SubSite site, string themeName)
        {
            site.Theme = themeName;
            foreach (var s in site.Sites)
            {
                UpdateChildrenSitesTheme(s, themeName);
            }
        }
    }
}
