// Z-005 subscription deployment: one Japan East evaluation resource group.
targetScope = 'subscription'

@description('Azure region for the resource group and Foundry resource.')
param location string = 'japaneast'

@description('Required resource group name from ADR-0006.')
param resourceGroupName string = 'HubRadio-MovieCreator'

@description('Globally unique Foundry resource name derived without user data.')
param accountName string

@minLength(36)
@maxLength(36)
@description('Microsoft Entra User object ID that receives inference-only data-plane access.')
param inferencePrincipalId string

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: {
    application: 'HubRadio-MovieCreator'
    environment: 'evaluation'
    managedBy: 'bicep'
    task: 'Z-005'
  }
}

module foundry 'foundry.bicep' = {
  name: 'hubradio-foundry-evaluation'
  scope: resourceGroup
  params: {
    accountName: accountName
    inferencePrincipalId: inferencePrincipalId
    location: location
  }
}

output resourceGroupId string = resourceGroup.id
output foundryAccountId string = foundry.outputs.accountId
output modelDeploymentId string = foundry.outputs.modelDeploymentId
output inferenceEndpoint string = foundry.outputs.inferenceEndpoint