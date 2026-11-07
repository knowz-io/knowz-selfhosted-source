import { describe, it, expect } from 'vitest'
import {
  describeDestination,
  describeLinkDestination,
  hasPeerConnections,
  hostOf,
  isKnowzCloudUrl,
  isPrimaryConnection,
  linkBelongsTo,
  primaryConnection,
  resolveLinkConnection,
} from '../lib/destinations'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../lib/types'

const base: Omit<PlatformConnectionDto, 'id' | 'platformApiUrl' | 'displayName'> = {
  hasApiKey: true,
  apiKeyMask: 'ukz_****ABCD',
  remoteTenantId: null,
  lastTestedAt: null,
  lastTestStatus: 'Untested',
  lastTestError: null,
  updatedAt: '2026-09-23T00:00:00Z',
}

const cloud: PlatformConnectionDto = {
  ...base,
  id: 'c-cloud',
  platformApiUrl: 'https://api.knowz.io',
  displayName: null,
}
const peer: PlatformConnectionDto = {
  ...base,
  id: 'c-peer',
  platformApiUrl: 'https://knowz.example.com/',
  displayName: 'Office instance',
}
const unnamedPeer: PlatformConnectionDto = {
  ...base,
  id: 'c-peer-2',
  platformApiUrl: 'https://Portable.Example.org',
  displayName: '  ',
}

const link = (over: Partial<VaultSyncStatusDto>): VaultSyncStatusDto => ({
  linkId: 'l',
  localVaultId: 'lv',
  localVaultName: 'Local',
  remoteVaultId: 'rv',
  platformApiUrl: 'https://api.knowz.io',
  status: 'Ok',
  lastSyncError: null,
  lastSyncCompletedAt: null,
  lastPullCursor: null,
  lastPushCursor: null,
  syncEnabled: true,
  ...over,
})

// ---------- UI_SH_PeerDestination : labels are display-only classification ----------
describe('destinations — describeDestination', () => {
  it('Should_LabelBuiltInCloudHosts_AsKnowzCloud', () => {
    expect(isKnowzCloudUrl('https://api.knowz.io')).toBe(true)
    expect(isKnowzCloudUrl('https://api.dev.knowz.io/')).toBe(true)
    expect(isKnowzCloudUrl('https://knowz.example.com')).toBe(false)
    const d = describeDestination(cloud)
    expect(d).toMatchObject({ kind: 'cloud', label: 'Knowz Cloud', kindLabel: 'Knowz Cloud' })
  })

  it('Should_PreferDisplayName_ForAnyKind', () => {
    expect(describeDestination(peer)).toMatchObject({
      kind: 'peer',
      label: 'Office instance',
      kindLabel: 'Allowlisted peer',
      host: 'knowz.example.com',
    })
    expect(describeDestination({ ...cloud, displayName: 'Prod' }).label).toBe('Prod')
  })

  it('Should_FallBackToHost_ForAnUnnamedPeer', () => {
    expect(describeDestination(unnamedPeer).label).toBe('portable.example.org')
  })

  it('Should_DescribeNull_AsKnowzCloud', () => {
    expect(describeDestination(null).label).toBe('Knowz Cloud')
  })

  it('Should_ParseHosts_Leniently', () => {
    expect(hostOf('knowz.example.com/path')).toBe('knowz.example.com')
    expect(hostOf('')).toBe('')
    expect(hostOf(undefined)).toBe('')
  })
})

describe('destinations — primary + link resolution', () => {
  it('Should_TreatTheHeadOfTheServerList_AsPrimary', () => {
    expect(primaryConnection([cloud, peer])).toBe(cloud)
    expect(primaryConnection([])).toBeNull()
    expect(isPrimaryConnection(cloud, [cloud, peer])).toBe(true)
    expect(isPrimaryConnection(peer, [cloud, peer])).toBe(false)
  })

  it('Should_MatchLinks_ByConnectionId_BeforeUrl', () => {
    const bound = link({ platformConnectionId: 'c-peer', platformApiUrl: 'https://api.knowz.io' })
    expect(linkBelongsTo(bound, peer)).toBe(true)
    expect(linkBelongsTo(bound, cloud)).toBe(false)
    expect(resolveLinkConnection(bound, [cloud, peer])).toBe(peer)
  })

  it('Should_MatchLegacyLinks_ByNormalisedUrl', () => {
    const legacy = link({ platformApiUrl: 'https://KNOWZ.example.com' })
    expect(resolveLinkConnection(legacy, [cloud, peer])).toBe(peer)
    expect(describeLinkDestination(legacy, [cloud, peer]).label).toBe('Office instance')
  })

  it('Should_LabelAnUnmatchedLink_FromItsOwnUrl', () => {
    const orphan = link({ platformApiUrl: 'https://gone.example.net' })
    expect(resolveLinkConnection(orphan, [cloud])).toBeNull()
    expect(describeLinkDestination(orphan, [cloud])).toMatchObject({
      kind: 'peer',
      label: 'gone.example.net',
    })
  })

  it('Should_DetectPeers_OnlyWhenANonCloudHostIsStored', () => {
    expect(hasPeerConnections([cloud])).toBe(false)
    expect(hasPeerConnections([cloud, peer])).toBe(true)
  })
})
