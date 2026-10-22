function ConvertTo-UsStateName {
    [CmdletBinding()]
    param([string] $State)

    if ([string]::IsNullOrWhiteSpace($State)) {
        return $null
    }

    $stateNamesByCode = @{
        AL = 'Alabama'; AK = 'Alaska'; AZ = 'Arizona'; AR = 'Arkansas'; CA = 'California'; CO = 'Colorado'; CT = 'Connecticut'; DE = 'Delaware'; FL = 'Florida'; GA = 'Georgia'
        HI = 'Hawaii'; ID = 'Idaho'; IL = 'Illinois'; IN = 'Indiana'; IA = 'Iowa'; KS = 'Kansas'; KY = 'Kentucky'; LA = 'Louisiana'; ME = 'Maine'; MD = 'Maryland'
        MA = 'Massachusetts'; MI = 'Michigan'; MN = 'Minnesota'; MS = 'Mississippi'; MO = 'Missouri'; MT = 'Montana'; NE = 'Nebraska'; NV = 'Nevada'; NH = 'New Hampshire'; NJ = 'New Jersey'
        NM = 'New Mexico'; NY = 'New York'; NC = 'North Carolina'; ND = 'North Dakota'; OH = 'Ohio'; OK = 'Oklahoma'; OR = 'Oregon'; PA = 'Pennsylvania'; RI = 'Rhode Island'; SC = 'South Carolina'
        SD = 'South Dakota'; TN = 'Tennessee'; TX = 'Texas'; UT = 'Utah'; VT = 'Vermont'; VA = 'Virginia'; WA = 'Washington'; WV = 'West Virginia'; WI = 'Wisconsin'; WY = 'Wyoming'
        DC = 'District of Columbia'
    }

    $normalizedState = $State.Trim()
    $upperState = $normalizedState.ToUpperInvariant()
    if ($stateNamesByCode.ContainsKey($upperState)) {
        return $stateNamesByCode[$upperState]
    }

    return $normalizedState
}

function Resolve-UsTemperatureLocation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $City,

        [string] $State
    )

    $geocodingResponse = Invoke-RestMethod -Uri "https://geocoding-api.open-meteo.com/v1/search?name=$([uri]::EscapeDataString($City))&count=10&language=en&format=json" -Method Get
    $candidateLocations = @($geocodingResponse.results | Where-Object { $_.country_code -eq 'US' })
    $resolvedStateName = ConvertTo-UsStateName -State $State

    if ($resolvedStateName) {
        $stateMatches = @($candidateLocations | Where-Object { $_.admin1 -eq $resolvedStateName -or $_.admin1 -like "*$resolvedStateName*" })
        if ($stateMatches.Count -gt 0) {
            $candidateLocations = $stateMatches
        }
    }

    if ($candidateLocations.Count -eq 0) {
        throw "Could not find a US city named '$City'."
    }

    return $candidateLocations[0]
}