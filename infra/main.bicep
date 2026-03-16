@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Base name for all resources')
param appName string = 'revu'

@secure()
@description('OpenAI API key')
param aiOpenAiKey string

@secure()
@description('Anthropic API key')
param aiAnthropicKey string

// ---------- Naming ----------

var suffix = uniqueString(resourceGroup().id)
var storageAccountName = toLower('st${appName}${suffix}')
var appInsightsName = 'appi-${appName}'
var logAnalyticsName = 'log-${appName}'
var planName = 'asp-${appName}'
var functionAppName = 'func-${appName}'
var cosmosAccountName = 'cosmos-${appName}'
var deploymentContainerName = 'app-package'

// ---------- Storage Account ----------

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: deploymentContainerName
}

// ---------- Log Analytics + App Insights ----------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ---------- Cosmos DB (Serverless) ----------

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosAccountName
  location: location
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [{ name: 'EnableServerless' }]
    locations: [{ locationName: location, failoverPriority: 0 }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
  }
}

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'revu'
  properties: {
    resource: { id: 'revu' }
  }
}

// ---------- Flex Consumption Plan ----------

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

// ---------- Function App ----------
// Git provider orgs (ADO, GitHub) are runtime config — add via:
//   az functionapp config appsettings set -n func-revu -g rg-revu-prod \
//     --settings "AzureDevOps__Organizations__<org>__Organization=<org>" \
//                "AzureDevOps__Organizations__<org>__PersonalAccessToken=<pat>"

var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: plan.id
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'StorageAccountConnectionString'
            storageAccountConnectionStringName: 'AzureWebJobsStorage'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 10
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage', value: storageConnectionString }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'Cosmos__ConnectionString', value: cosmosAccount.listConnectionStrings().connectionStrings[0].connectionString }
        { name: 'Ai__Models__default', value: 'anthropic/claude-haiku-4-5' }
        { name: 'Ai__Models__reasoning', value: 'anthropic/claude-haiku-4-5' }
        { name: 'Ai__OpenAI__ApiKey', value: aiOpenAiKey }
        { name: 'Ai__Anthropic__ApiKey', value: aiAnthropicKey }
        { name: 'Revu__EnableIncrementalReviews', value: 'true' }
        { name: 'Revu__EnableCodeGraph', value: 'true' }
        { name: 'Revu__EnableChat', value: 'false' }
        { name: 'ReviewQueue', value: 'review-queue' }
        { name: 'ChatQueue', value: 'chat-queue' }
        { name: 'IndexQueue', value: 'index-queue' }
      ]
    }
  }
}

// ---------- Outputs ----------

output functionAppName string = functionApp.name
output functionAppDefaultHostName string = functionApp.properties.defaultHostName
