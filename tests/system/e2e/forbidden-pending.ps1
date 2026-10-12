#Requires -Version 7.0
# FR-064 AC-041 / FR-132 AC-139 (system): 権限がない利用者の forbidden。
# 権限のない利用者を再現する方法が要求定義書にない（TBD）ため、台帳ではブロックしている。
# 解決するまで、このスクリプトは意図的に失敗する（pass と誤判定しないため）。
Write-Error 'permission-less user scenario is TBD in requirements (see ledger block reason)'
exit 2
