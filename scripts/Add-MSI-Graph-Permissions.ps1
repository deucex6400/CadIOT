# Inputs 
$ManagedIdentityObjectId = "4a321918-8496-4d61-a804-3307cee60d91" # MSI service principal Object ID 
$GraphAppId = "00000003-0000-0000-c000-000000000000" #Microsoft Graph 

# Get service principals 
$miSp = Get-MgServicePrincipal -ServicePrincipalId $ManagedIdentityObjectId 
$graphSp = Get-MgServicePrincipal -Filter "appId eq '$GraphAppId'" 

# Resolve Graph app roles 
$appRole_MailRead = $graphSp.AppRoles | Where-Object { $_.Value -eq "Mail.Read" -and $_.AllowedMemberTypes -contains "Application" } 
 
$appRole_SubscriptionReadAll = $graphSp.AppRoles | Where-Object { $_.Value -eq "Subscription.Read.All" -and $_.AllowedMemberTypes -contains "Application" }
$appRole_SubscriptionRWAll = $graphSp.AppRoles | Where-Object { $_.Value -eq "Subscription.ReadWrite.All" -and $_.AllowedMemberTypes -contains "Application" }

# Assign roles 
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $miSp.Id -PrincipalId $miSp.Id -ResourceId $graphSp.Id -AppRoleId $appRole_MailRead.Id
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $miSp.Id -PrincipalId $miSp.Id -ResourceId $graphSp.Id -AppRoleId $appRole_SubscriptionReadAll.Id
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $miSp.Id -PrincipalId $miSp.Id -ResourceId $graphSp.Id -AppRoleId $appRole_SubscriptionRWAll.Id

# Verify 
Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $miSp.Id