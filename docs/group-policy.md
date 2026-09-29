# Group Policy

On domain-joined servers, firewall policy is usually managed centrally, and Group Policy can stop locally created firewall rules from having any effect. This page explains how to deploy the inbound rules Caddy Proxy Manager needs through a GPO, how to get a domain-joined server onto the Domain network profile, and how to distribute Caddy's internal root certificate. Every signed-in role can see and download the script once the Readiness checks have run; running it needs rights to create and link GPOs in the domain.

## Why use a GPO

- A GPO can set **Apply local firewall rules** to **No**. Rules created on the server, including those created by the Readiness **Fix** button, are then ignored.
- Rules delivered by a GPO always apply, survive a server rebuild and are visible to the team that manages the firewall policy.

The Readiness check **Local firewall rules honoured** tells you when local rules are ignored, and the port checks say which rules are missing for which network profile. See [Readiness](readiness.md).

## Get the script

1. Open **Server › Readiness**. If the checks have not run yet, select **Run checks** (Operator or Admin); the page needs a report to know the domain and the computer account.
2. On a domain-joined server, the **Group Policy firewall script** card appears next to the checks.
3. Select **Download** to save the script as `Caddy-Firewall-GPO.ps1`, or copy it.

The same script is in the PowerShell block of the **Firewall rules via Group Policy** check (expand the row on the Readiness page). It is generated from the current settings: it contains one rule per port the server needs (Caddy HTTP and HTTPS, UDP HTTPS when HTTP/3 is on, the web UI port and UI HTTPS port unless the UI listens on loopback, and every enabled stream). Download it again after you change ports, add streams or turn HTTP/3 on.

## Requirements for running it

- A domain controller, or a management server or admin workstation with the Group Policy Management tools (RSAT) installed. Member servers usually do not have them.
- **Windows PowerShell 5.1**. The script does not run in PowerShell 7.
- An account allowed to create and link GPOs, for example a member of Domain Admins. On a domain controller, use an elevated prompt.

If the GroupPolicy module is missing, the script stops and names the RSAT feature to install.

## Run it

Preview what it would do, then run it:

```powershell
.\Caddy-Firewall-GPO.ps1 -WhatIf
.\Caddy-Firewall-GPO.ps1
```

The script is safe to run repeatedly. It:

1. Creates the GPO **Caddy Proxy Manager - Firewall**, or reuses it when it exists, and disables its user settings.
2. Replaces the inbound allow rules inside the GPO, in the rule group **Caddy Proxy Manager**, for all profiles.
3. Limits the GPO to this computer through security filtering. Authenticated Users keep read access, which Group Policy requires.
4. Links the GPO to the OU that contains the computer account, or enables an existing disabled link. When the computer is still in a container such as the default `CN=Computers`, which cannot have GPO links, it links the GPO to the domain root instead; security filtering still limits it to this computer.

### Parameters

| Parameter | Default | Description |
|---|---|---|
| `-GpoName` | `Caddy Proxy Manager - Firewall` | Name of the GPO. |
| `-Domain` | The server's domain | DNS name of the Active Directory domain. |
| `-TargetOU` | The OU of the server's computer account | Where to link the GPO. Empty means no link; the script then prints the command to link it yourself. |
| `-ComputerName` | This server | The computer the rules are for. |
| `-RestrictToThisComputer` | `$true` | `$false` applies the GPO to every computer in the OU. |

When the server did not report a domain, fill in `-Domain` and `-TargetOU` before running the script, or create the rules locally with Readiness **Fix** instead.

## After running it

1. On the server, refresh Group Policy:

```powershell
gpupdate /target:computer /force
```

From another machine you can use `Invoke-GPUpdate -Computer <server> -Target Computer -RandomDelayInMinutes 0 -Force`.

2. Check that the rules arrived:

```powershell
Get-NetFirewallRule -PolicyStore ActiveStore -Group 'Caddy Proxy Manager' | Format-Table DisplayName, Enabled, PolicyStoreSourceType
```

3. Select **Run checks** on the **Readiness** page. The firewall checks now name the GPO rules.

## Network profile on domain-joined servers

A domain-joined server's network connections should be **DomainAuthenticated**, otherwise the Domain firewall profile and GPO rules scoped to it do not apply. The Readiness check **Network profile** warns when a connection shows Public or Private instead. This means Network Location Awareness could not reach a domain controller when the connection came up.

1. Check that the adapter's DNS servers are the domain controllers.
2. Check that a domain controller is reachable, for example with `nltest /dsgetdc:<domain>`.
3. Restart Network Location Awareness:

```powershell
Restart-Service NlaSvc -Force
```

4. Run the Readiness checks again.

The category of a domain-joined connection cannot be changed by hand, so Readiness offers no **Fix** for it. On a workgroup server, a Public connection can be switched to Private with **Fix**.

## Distribute the internal root certificate

If any host uses the **Internal CA** certificate option, browsers trust its sites only when Caddy's root certificate is in their **Trusted Root Certification Authorities** store. Readiness reminds you with the **Internal CA root distribution** check. The root is valid for 10 years; Caddy renews the intermediate certificate automatically.

Download the root certificate from the **Certificates** page (see [Certificates](certificates.md)). It is stored on the server at `C:\ProgramData\CaddyProxyManager\caddy\data\pki\authorities\local\root.crt`. Then deploy it with one of these:

- **Group Policy**: in Group Policy Management, edit a GPO linked to the client OUs, go to **Computer Configuration › Policies › Windows Settings › Security Settings › Public Key Policies › Trusted Root Certification Authorities**, and **Import** the file.
- **Active Directory**: publish it so that every domain member trusts it. This needs Enterprise Admins rights:

```powershell
certutil -dspublish -f root.crt RootCA
```

## Related

- [Readiness](readiness.md)
- [Installation](installation.md#firewall-rules)
- [Certificates](certificates.md)
- [Security](security.md)
