import type {
  BackupSettings,
  BackupSettingsInput,
  CaddySettings,
  CaddySettingsInput,
  LdapSettings,
  LdapSettingsInput,
  NotificationSettings,
  NotificationSettingsInput,
  UiSettings,
  UiSettingsInput,
} from './types';

// Convert a settings GET shape into a PUT body: drop the output-only has<Name> flags.
// Write-only secrets are left absent (= unchanged) unless the caller sets them.

export function caddySettingsInput(s: CaddySettings): CaddySettingsInput {
  const { hasEabMacKey: _h, hasAcmeIssuerJson: _a, ...rest } = s;
  // The API omits null values; keep the field explicit so clearing it is sent as null.
  return { ...rest, publicHttpsPort: rest.publicHttpsPort ?? null };
}

export function notificationSettingsInput(s: NotificationSettings): NotificationSettingsInput {
  const { hasSmtpPassword: _h, hasOAuthClientSecret: _o, ...rest } = s;
  return rest;
}

export function uiSettingsInput(s: UiSettings): UiSettingsInput {
  const { hasHttpsPfxPassword: _h, ...rest } = s;
  return rest;
}

export function ldapSettingsInput(s: LdapSettings): LdapSettingsInput {
  const { hasBindPassword: _h, ...rest } = s;
  return rest;
}

export function backupSettingsInput(s: BackupSettings): BackupSettingsInput {
  const { hasPassword: _h, ...rest } = s;
  return rest;
}
