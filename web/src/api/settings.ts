import type { CaddySettings, CaddySettingsInput, NotificationSettings, NotificationSettingsInput, UiSettings, UiSettingsInput } from './types';

// Convert a settings GET shape into a PUT body: drop the output-only has<Name> flags.
// Write-only secrets are left absent (= unchanged) unless the caller sets them.

export function caddySettingsInput(s: CaddySettings): CaddySettingsInput {
  const { hasEabMacKey: _h, ...rest } = s;
  return rest;
}

export function notificationSettingsInput(s: NotificationSettings): NotificationSettingsInput {
  const { hasSmtpPassword: _h, ...rest } = s;
  return rest;
}

export function uiSettingsInput(s: UiSettings): UiSettingsInput {
  const { hasHttpsPfxPassword: _h, ...rest } = s;
  return rest;
}
