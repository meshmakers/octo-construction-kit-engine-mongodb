# Read the contents of the first JSON file
$jsonAttributes = Get-Content -Raw -Path "./ck-attributes.json" | ConvertFrom-Json

# Read the contents of the first JSON file
$jsonAssocs = Get-Content -Raw -Path "./ck-associations.json" | ConvertFrom-Json

# Read the content of entities
$base = Get-Content -Raw -Path "./ck-entities-base.json" | ConvertFrom-Json
$query = Get-Content -Raw -Path "./ck-entities-query.json" | ConvertFrom-Json
$servicehook = Get-Content -Raw -Path "./ck-entities-servicehook.json" | ConvertFrom-Json
$jsonEntities = $base.entities + $query.entities + $servicehook.entities


# Create a new object and combine the properties from each JSON
Write-Host "Merging JSON files..."
$mergedJson = [PSCustomObject]@{
    name = @{
        id= "System" 
        version= "1.0.0"
    }
    associationRoles = $jsonAssocs.associationRoles
    attributes = $jsonAttributes.attributes
    entities = $jsonEntities
}

# Convert the merged JSON back to a string
Write-Host "Converting to JSON..."
$mergedJsonString = $mergedJson | ConvertTo-Json -Depth 10

# Save the merged JSON to a new file
Write-Host "Saving merged JSON to file..."
$mergedJsonString | Out-File -FilePath "./ck-system.json"