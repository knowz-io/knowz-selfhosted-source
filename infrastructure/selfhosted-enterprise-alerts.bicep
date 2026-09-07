// ============================================================================
// selfhosted-enterprise-alerts.bicep
//
// Deploys an action group + 3 scheduled-query / metric alerts targeting an
// enterprise self-hosted deployment. Deployed separately from the main
// enterprise template (customers may bring their own alerting stack).
//
// Spec: knowzcode/specs/SH_ENTERPRISE_SKILL.md Section 2.3
// NodeID: DOC_EnterpriseRunbook (alerts bundled with runbook)
// WorkGroup: kc-feat-sh-enterprise-deploy-20260418-144500
// ============================================================================

targetScope = 'resourceGroup'

@description('Azure region for regional resources (action group is always global).')
param location string = resourceGroup().location

@description('Resource name prefix (e.g. `knowz-sh-ent`). Used to namespace the action group and alerts.')
param prefix string = 'knowz-sh-ent'

@description('Email address of the enterprise customer\'s AAD admin (captured during skill Phase 1.5). Receives all alert notifications.')
param aadAdminEmail string

@description('Name of the SQL Server (logical) whose database is monitored.')
param sqlServerName string

@description('Name of the SQL Database monitored for DTU saturation.')
param sqlDatabaseName string

@description('Name of the Container App Environment whose console logs are monitored.')
param containerAppEnvironmentName string = ''

@description('Resource ID of the Log Analytics Workspace used for KQL-based container log alerts.')
param logAnalyticsWorkspaceId string

@description('Resource tags to apply to all alert resources.')
param tags object = {}

// ----------------------------------------------------------------------------
// Derived resource IDs (safer than accepting opaque strings from params).
// ----------------------------------------------------------------------------
var sqlDatabaseId = resourceId('Microsoft.Sql/servers/databases', sqlServerName, sqlDatabaseName)
var actionGroupName = '${prefix}-ag-critical'

// ============================================================================
// Action group — emails the enterprise customer's AAD admin only.
// Knowz Ops are NOT on the default notification list (Rule 6 of
// SH_ENTERPRISE_SKILL.md: enterprise customers own their data plane).
// ============================================================================
resource actionGroup 'Microsoft.Insights/actionGroups@2023-09-01-preview' = {
  name: actionGroupName
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'knowzshent'
    enabled: true
    emailReceivers: [
      {
        name: 'aad-admin'
        emailAddress: aadAdminEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// ============================================================================
// Alert 1 — Critical container logs (severity 1)
// Threshold: >0 Critical entries in 5 min, evaluated every 5 min.
// Per SH_ENTERPRISE_SKILL.md Section 2.3 Alert 1.
// ============================================================================
resource criticalLogsAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: '${prefix}-critical-logs'
  location: location
  tags: tags
  properties: {
    displayName: '${prefix} critical container logs'
    description: 'Fires when any Container App in the enterprise self-hosted deployment emits a Critical log entry in a 5-minute window.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [ logAnalyticsWorkspaceId ]
    criteria: {
      allOf: [
        {
          query: 'ContainerAppConsoleLogs_CL\n| where LogLevel_s == "Critical"\n| where ContainerAppName_s contains "${prefix}" or ContainerAppName_s startswith "knowz-sh"'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [ actionGroup.id ]
    }
  }
}

// ============================================================================
// Alert 2 — SQL DTU > 80% for 10 min (severity 2)
// Per SH_ENTERPRISE_SKILL.md Section 2.3 Alert 2.
// ============================================================================
resource sqlDtuAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: '${prefix}-sql-dtu'
  location: 'global'
  tags: tags
  properties: {
    description: 'Fires when SQL DTU consumption averages over 80% for 10 minutes — indicates saturation that will stall enrichment processing.'
    severity: 2
    enabled: true
    scopes: [ sqlDatabaseId ]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'dtu-pct'
          metricNamespace: 'Microsoft.Sql/servers/databases'
          metricName: 'dtu_consumption_percent'
          operator: 'GreaterThan'
          threshold: 80
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
        }
      ]
    }
    actions: [
      {
        actionGroupId: actionGroup.id
      }
    ]
  }
}

// ============================================================================
// Alert 3 — EnrichmentOutbox failure rate > 5% over 15 minutes (severity 2)
// Queries Log Analytics for container logs containing EnrichmentOutbox +
// Failed. Uses containerAppEnvironmentName param (optional) to scope the
// query tighter when supplied.
// Per SH_ENTERPRISE_SKILL.md Section 2.3 Alert 3.
// ============================================================================
var envFilter = empty(containerAppEnvironmentName) ? '' : '\n| where _ResourceId contains "${containerAppEnvironmentName}"'

resource outboxFailureAlert 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: '${prefix}-outbox-failure-rate'
  location: location
  tags: tags
  properties: {
    displayName: '${prefix} EnrichmentOutbox failure rate'
    description: 'Fires when EnrichmentOutbox failure rate exceeds 5% over a 15-minute window — indicates AI service quota or schema regression affecting enrichment pipeline.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ logAnalyticsWorkspaceId ]
    criteria: {
      allOf: [
        {
          query: 'ContainerAppConsoleLogs_CL\n| where Log_s contains "EnrichmentOutbox"${envFilter}\n| extend isFailure = iff(Log_s contains "Failed", 1, 0)\n| summarize failures = sum(isFailure), total = count()\n| extend rate = iff(total > 0, todouble(failures) / todouble(total) * 100.0, 0.0)\n| where rate > 5.0 and total >= 5'
          timeAggregation: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [ actionGroup.id ]
    }
  }
}

output actionGroupId string = actionGroup.id
output criticalLogsAlertId string = criticalLogsAlert.id
output sqlDtuAlertId string = sqlDtuAlert.id
output outboxFailureAlertId string = outboxFailureAlert.id
