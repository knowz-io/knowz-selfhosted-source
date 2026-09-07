// infrastructure/modules/assert.bicep
// Fail-fast assertion helper — when invoked with `if (precondition_violated)` from a parent template,
// forces an ARM template-evaluation error so the deployment fails before any resources are created.
//
// Usage:
//   module assertX 'modules/assert.bicep' = if (some_invalid_state) {
//     name: 'assertX'
//     params: { message: 'Reason for failure' }
//   }
//
// The error surfaces as a deployment validation failure with the message embedded in the trigger.

@description('Diagnostic message describing the violated precondition. Surfaced in deployment errors.')
param message string

// Force an ARM template-evaluation error: indexing into an empty array is rejected by ARM
// at template-compile time, before any resource is provisioned. The message param is referenced
// in the error path so it appears in the deployment error log.
output assertionFailed string = '${message} :: ${string(json('[]')[0])}'
