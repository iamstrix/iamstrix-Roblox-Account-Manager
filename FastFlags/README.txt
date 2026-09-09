Roblox Account Manager - Group FastFlags
========================================
Place JSON files in this folder named after your Account Groups.
When an account launches, RAM will automatically apply the matching FastFlags to Roblox & Bloxstrap!

Matching order for an account in group '01 Alts':
  1. 01 Alts.json   (Exact group name match)
  2. Alts.json      (Name with leading numbers stripped)
  3. Default.json   (Fallback for any account)

Example JSON content (standard ClientAppSettings.json format):
{
  "FFlagHandleAltEnterFullscreenManually": "False",
  "DFIntTextureQualityOverride": "0"
}
