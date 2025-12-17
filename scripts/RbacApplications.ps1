
param(
  [Parameter(Mandatory=$true)] [string] $MailboxSmtp,
  [Parameter(Mandatory=$true)] [string] $AppId,
  [Parameter(Mandatory=$true)] [string] $ObjectId,
  [string] $ScopeName = "BGVFD-Dispatch-Scope",
  [string] $RoleAssignmentName = "BGVFD-Dispatch-Graph",
  [string] $RoleName = "Application Mail.Read"
)

Connect-ExchangeOnline
New-ManagementScope -Name $ScopeName -RecipientRestrictionFilter "PrimarySmtpAddress -eq '$MailboxSmtp'" -ErrorAction SilentlyContinue
New-ServicePrincipal -AppId $AppId -ObjectId $ObjectId -DisplayName "BGVFD-Dispatch-SP" -ErrorAction SilentlyContinue
New-ManagementRoleAssignment -Name $RoleAssignmentName -Role $RoleName -App $AppId -CustomResourceScope $ScopeName -ErrorAction SilentlyContinue
Write-Host "RBAC configuration completed" -ForegroundColor Green
