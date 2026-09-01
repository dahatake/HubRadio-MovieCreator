// Z-005 minimal Foundry resource. No project, storage, search, agent, or secret.
targetScope = 'resourceGroup'

@description('Azure region for the Foundry resource.')
param location string

@description('Globally unique Foundry resource and custom subdomain name.')
param accountName string

@minLength(36)
@maxLength(36)
@description('Microsoft Entra User object ID that receives inference-only access.')
param inferencePrincipalId string

var deploymentName = 'gpt-5-6-sol'
var openAiUserRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
)

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
  tags: {
    application: 'HubRadio-MovieCreator'
    environment: 'evaluation'
    managedBy: 'bicep'
    task: 'Z-005'
  }
}

resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: deploymentName
  sku: {
    name: 'DataZoneStandard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.6-sol'
      version: '2026-07-09'
    }
    raiPolicyName: 'Microsoft.DefaultV2'
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource inferenceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, inferencePrincipalId, openAiUserRoleDefinitionId)
  scope: account
  properties: {
    principalId: inferencePrincipalId
    principalType: 'User'
    roleDefinitionId: openAiUserRoleDefinitionId
  }
}

output accountId string = account.id
output modelDeploymentId string = modelDeployment.id
output inferenceEndpoint string = account.properties.endpoint
