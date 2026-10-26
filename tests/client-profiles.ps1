# Run after a Release build: powershell -NoProfile -File tests/client-profiles.ps1
# Uses only temporary fixtures; does not launch Roblox or edit installed client settings.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$projectDirectory = Join-Path $repo 'RBX Alt Manager'
[xml]$project = Get-Content (Join-Path $projectDirectory 'RBX Alt Manager.csproj')
foreach ($reference in $project.Project.ItemGroup.Reference) {
    if ($reference.HintPath) {
        $dependency = [IO.Path]::GetFullPath((Join-Path $projectDirectory ([string]$reference.HintPath)))
        if (Test-Path -LiteralPath $dependency) { try { [void][Reflection.Assembly]::LoadFrom($dependency) } catch { } }
    }
}
Add-Type -AssemblyName System.Windows.Forms
[void][Reflection.Assembly]::LoadFrom((Join-Path $repo 'build/Release/Roblox Account Manager.exe'))
$originalDirectory = [Environment]::CurrentDirectory
$fixture = Join-Path $PSScriptRoot ('.scratch-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$script:passed = 0
function Assert($condition, [string]$message) {
    if (!$condition) { throw "FAIL: $message" }
    $script:passed++
    Write-Output "PASS: $message"
}
function Invoke-Internal($method, [object[]]$arguments) {
    for ($index = 0; $index -lt $arguments.Length; $index++) {
        if ($null -ne $arguments[$index] -and $null -ne $arguments[$index].PSObject.BaseObject) { $arguments[$index] = $arguments[$index].PSObject.BaseObject }
    }
    return ,($method.Invoke($null, $arguments))
}
$internal = [Reflection.BindingFlags]'Static,NonPublic'
try {
    [Environment]::CurrentDirectory = $fixture
    [RBX_Alt_Manager.AccountManager]::General = New-Object RBX_Alt_Manager.IniSection 'General'
    $general = [RBX_Alt_Manager.AccountManager]::General
    $general.Set('GroupFastFlags', 'true')
    $general.Set('UseAltClient', 'true')
    $general.Set('UnlockFPS', 'false')
    [RBX_Alt_Manager.AccountManager]::AccountsList = New-Object 'System.Collections.Generic.List[RBX_Alt_Manager.Account]'
    $main = New-Object RBX_Alt_Manager.Account
    $main.Group = ' 01 Main '
    $alt = New-Object RBX_Alt_Manager.Account
    $alt.Group = '02 Alts'
    [RBX_Alt_Manager.AccountManager]::AccountsList.Add($main)
    [RBX_Alt_Manager.AccountManager]::AccountsList.Add($alt)
    $flagsDirectory = [RBX_Alt_Manager.Classes.ClientSettingsPatcher]::FastFlagsDirectory
    [void][IO.Directory]::CreateDirectory($flagsDirectory)
    [IO.File]::WriteAllText((Join-Path $flagsDirectory 'Default.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $flagsDirectory 'Alts.json'), '{"AltFlag":"True","_RobloxSettings":{"FramerateCap":30}}')
    Assert ([RBX_Alt_Manager.Classes.ClientProfiles]::GetRole($main).ToString() -eq 'Main') 'Numbered main wins over Default.json'
    $find = [RBX_Alt_Manager.Classes.ClientSettingsPatcher].GetMethod('FindGroupProfile', $internal)
    Assert ($null -eq (Invoke-Internal $find @($main))) 'Main never receives a profile'
    Assert ([IO.Path]::GetFileName((Invoke-Internal $find @($alt))) -eq 'Alts.json') 'Number prefix uses cleaned group profile'
    [IO.File]::WriteAllText((Join-Path $flagsDirectory '02 Alts.json'), '{}')
    Assert ([IO.Path]::GetFileName((Invoke-Internal $find @($alt))) -eq '02 Alts.json') 'Exact group profile takes precedence'
    Assert ([RBX_Alt_Manager.Classes.ClientProfiles]::GetRole($null).ToString() -eq 'Normal') 'Unknown processes are Normal'
    $general.Set('GroupFastFlags', 'false')
    Assert ([RBX_Alt_Manager.Classes.ClientProfiles]::GetRole($alt).ToString() -eq 'Normal') 'GroupFastFlags off disables alt role'
    $general.Set('GroupFastFlags', 'true')
    $general.Set('MainGroup', '99 Primary')
    $main.Group = 'primary'
    Assert ([RBX_Alt_Manager.Classes.ClientProfiles]::GetRole($main).ToString() -eq 'Main') 'Configured main group is normalized without case sensitivity'
    Assert ([Utilities]::GetBrowserTrackerId('RobloxPlayerBeta.exe --app -b 123 -t ticket') -eq '123') 'Switch tracker parsing'
    Assert ([Utilities]::GetBrowserTrackerId('roblox-player:1+BrowserTrackerId:456+channel:') -eq '456') 'URI tracker parsing'
    Assert ([Utilities]::GetBrowserTrackerId('browserTrackerId=789&placeId=1') -eq '789') 'URL tracker parsing'
    Assert ([Utilities]::GetBrowserTrackerId($null) -eq '') 'Missing tracker is safe'
    Assert ($null -eq [RBX_Alt_Manager.Classes.ClientProfiles]::FindClientSource('../version-invalid')) 'Reject invalid version paths'
    $exe = $null; $reason = $null
    Assert (![RBX_Alt_Manager.Classes.AltClient]::TryPrepare($alt, [ref]$exe, [ref]$reason)) 'Unverified direct arguments keep fallback'
    Assert ($reason -like '*main*') 'Fallback explains required main launch'

    $xmlPath = Join-Path $fixture 'settings.xml'
    [IO.File]::WriteAllText($xmlPath, '<roblox><Item><Properties><token name="SavedQualityLevel">9</token><float name="MasterVolume">0.5</float><bool name="Fullscreen">true</bool><string name="Unrelated">keep</string></Properties></Item></roblox>')
    $values = [Newtonsoft.Json.Linq.JObject]::Parse('{"SavedQualityLevel":1,"MasterVolume":0.25,"Fullscreen":false,"Unknown":2}')
    $apply = [RBX_Alt_Manager.Classes.ClientSettingsPatcher].GetMethod('ApplyGlobalBasicSettings', $internal)
    $oldCulture = [Threading.Thread]::CurrentThread.CurrentCulture
    try {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('fr-FR')
        [void](Invoke-Internal $apply @($xmlPath, $values))
    } finally { [Threading.Thread]::CurrentThread.CurrentCulture = $oldCulture }
    [xml]$xml = [IO.File]::ReadAllText($xmlPath)
    Assert ($xml.SelectSingleNode('//*[@name="MasterVolume"]').InnerText -eq '0.25') 'XML floats remain invariant across cultures'
    Assert ($xml.SelectSingleNode('//*[@name="Fullscreen"]').InnerText -eq 'false') 'XML boolean serialization'
    Assert ($xml.SelectSingleNode('//*[@name="Unrelated"]').InnerText -eq 'keep' -and !$xml.SelectSingleNode('//*[@name="Unknown"]')) 'XML preserves unrelated values and skips unknown keys'
    $readMain = [RBX_Alt_Manager.Classes.ClientProfiles].GetMethod('ReadMainSettings', $internal)
    $previous = [Newtonsoft.Json.Linq.JObject]::Parse('{"Fullscreen":true}')
    $captured = Invoke-Internal $readMain @($xmlPath, $previous)
    Assert ($captured.SavedQualityLevel.ToString() -eq '1' -and $captured.MasterVolume.ToString() -eq '0.25') 'Main snapshot reads XML properties'
    Assert ($captured.Fullscreen.ToString() -eq 'false' -and $null -eq $captured.Unrelated) 'Snapshot includes user-listed keys and excludes unrelated settings'
    $matchesAlt = [RBX_Alt_Manager.Classes.ClientProfiles].GetMethod('MatchesAltSettings', $internal)
    [IO.File]::WriteAllText((Join-Path $flagsDirectory '02 Alts.json'), '{"_RobloxSettings":{"MasterVolume":0.25,"SavedQualityLevel":1}}')
    Assert (Invoke-Internal $matchesAlt (,$captured)) 'Automatic snapshot recognizes numeric alt values despite XML string types'
    $different = [Newtonsoft.Json.Linq.JObject]::Parse('{"MasterVolume":"0.5","SavedQualityLevel":"9"}')
    Assert (!(Invoke-Internal $matchesAlt (,$different))) 'Automatic snapshot accepts distinct main settings'

    $shared = Join-Path $fixture 'shared'
    [void][IO.Directory]::CreateDirectory($shared)
    $sharedFlags = Join-Path $shared 'ClientAppSettings.json'
    $originalFlags = '{ "UserFlag": "original", "AltFlag": "user" }'
    [IO.File]::WriteAllText($sharedFlags, $originalFlags)
    $profile = [Newtonsoft.Json.Linq.JObject]::Parse('{"AltFlag":"profile","_RobloxSettings":{"FramerateCap":30}}')
    $dirs = New-Object 'System.Collections.Generic.List[string]'
    $dirs.Add($shared)
    $applyProfile = [RBX_Alt_Manager.Classes.ClientSettingsPatcher].GetMethod('ApplyProfile', $internal)
    [void](Invoke-Internal $applyProfile @($alt, 'Alts.json', $profile, $dirs))
    $merged = [IO.File]::ReadAllText($sharedFlags) | ConvertFrom-Json
    Assert ($merged.UserFlag -eq 'original' -and $merged.AltFlag -eq 'profile' -and !$merged._RobloxSettings) 'Fallback merges flags and strips reserved settings'
    Assert (Test-Path -LiteralPath (Join-Path $flagsDirectory '.backup.json')) 'Fallback persists recovery backup'
    [RBX_Alt_Manager.Classes.ClientSettingsPatcher]::RestoreOriginals()
    Assert ([IO.File]::ReadAllText($sharedFlags) -ceq $originalFlags) 'Fallback restores original bytes'
    Assert (!(Test-Path -LiteralPath (Join-Path $flagsDirectory '.backup.json'))) 'Successful recovery removes backup'
    $backupPath = Join-Path $flagsDirectory '.backup.json'
    [IO.File]::WriteAllText($backupPath, '{broken recovery JSON')
    [RBX_Alt_Manager.Classes.ClientSettingsPatcher]::RestoreOriginals()
    Assert ([IO.File]::ReadAllText($backupPath) -ceq '{broken recovery JSON') 'Unreadable recovery data is preserved'
    Remove-Item -LiteralPath $backupPath

    $source = Join-Path $fixture 'source'
    $target = Join-Path $fixture 'target'
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'ClientSettings'))
    [void][IO.Directory]::CreateDirectory($target)
    [IO.File]::WriteAllText((Join-Path $source 'binary.dat'), 'original')
    [IO.File]::WriteAllText((Join-Path $source 'AppSettings.xml'), '<settings/>')
    [IO.File]::WriteAllText((Join-Path $source 'ClientSettings/ClientAppSettings.json'), '{"MainFlag":true}')
    $copy = [RBX_Alt_Manager.Classes.AltClient].GetMethod('CopyTree', $internal)
    $copyArguments = @($source, $target, $false)
    [void](Invoke-Internal $copy $copyArguments)
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'ClientSettings'))) 'Alt clone excludes user ClientSettings'
    [IO.File]::WriteAllText((Join-Path $target 'AppSettings.xml'), '<changed/>')
    Assert ([IO.File]::ReadAllText((Join-Path $source 'AppSettings.xml')) -eq '<settings/>') 'AppSettings.xml is independent of source'
    if (!$copyArguments[2]) {
        [IO.File]::WriteAllText((Join-Path $target 'binary.dat'), 'changed')
        Assert ([IO.File]::ReadAllText((Join-Path $source 'binary.dat')) -eq 'changed') 'Ordinary client files are hard linked'
    } else { Write-Output 'INFO: Hard links unavailable; copy fallback used' }
    $delete = [RBX_Alt_Manager.Classes.AltClient].GetMethod('DeleteOwnedFolder', $internal)
    $rejected = $false
    try { [void](Invoke-Internal $delete @($source)) } catch { $rejected = $true }
    Assert ($rejected -and (Test-Path -LiteralPath $source)) 'Cleanup rejects folders outside AltClient root'
    $observe = [RBX_Alt_Manager.Classes.ClientProfiles].GetMethod('ObserveMainCommandLine', $internal)
    $direct = [RBX_Alt_Manager.Classes.ClientProfiles].GetMethod('GetDirectArguments', $internal)
    $uri = 'roblox-player:1+launchmode:play+gameinfo:new-ticket+launchtime:200+placelauncherurl:https%3a%2f%2fexample.invalid%2fjoin%3fx%3d1%26y%3d2+browsertrackerid:456+channel:'
    [void](Invoke-Internal $observe @('"C:\Roblox\RobloxPlayerBeta.exe" roblox-player:1+gameinfo:old-ticket+browsertrackerid:123'))
    Assert ((Invoke-Internal $direct @($uri)) -ceq $uri) 'Observed raw URI format is passed unchanged'
    [void](Invoke-Internal $observe @('"C:\Roblox\RobloxPlayerBeta.exe" --app -t old-ticket -j "https://example.invalid/old" -b 123 --launchtime=100'))
    Assert ([RBX_Alt_Manager.Classes.ClientProfiles]::VerifiedArgumentFormat -eq 'Switches') 'Recognizes documented Bloxstrap switch shape'
    $arguments = Invoke-Internal $direct @($uri)
    Assert ($arguments -eq '--app -t "new-ticket" -j "https://example.invalid/join?x=1&y=2" -b "456" --launchtime=200') 'Direct switch format receives new ticket, decoded URL, tracker and launch time'
    $templateField = [RBX_Alt_Manager.Classes.ClientProfiles].GetField('ArgumentTemplate', $internal)
    Assert ($templateField.GetValue($null) -notlike '*old-ticket*' -and $templateField.GetValue($null) -notlike '*example.invalid*') 'Observed template does not retain main ticket or join URL'
    [void](Invoke-Internal $observe @('"C:\Roblox\RobloxPlayerBeta.exe" --unrecognized secret -t ticket -j url -b 123'))
    Assert ($null -eq [RBX_Alt_Manager.Classes.ClientProfiles]::VerifiedArgumentFormat) 'Unknown argument shapes return to protocol fallback'
    Write-Output "$script:passed checks passed. No live Roblox or user settings changed."
} finally {
    [Environment]::CurrentDirectory = $originalDirectory
    $resolved = [IO.Path]::GetFullPath($fixture)
    $expectedParent = [IO.Path]::GetFullPath($PSScriptRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $expectedParent -and [IO.Path]::GetFileName($resolved).StartsWith('.scratch-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
