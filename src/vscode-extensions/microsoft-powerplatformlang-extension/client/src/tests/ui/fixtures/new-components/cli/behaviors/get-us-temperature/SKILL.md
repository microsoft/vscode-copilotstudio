---
name: get-us-temperature
description: 'MD2-162228 - Get the current observed temperature for any city or town in the United States. Use when the user asks for the current temperature, how hot or cold it is, or the temperature in a US city, e.g. "what is the temperature in Seattle" or "how cold is it in Austin right now".'
argument-hint: 'US city name, optionally with state (e.g. "Portland, OR")'
---

# Get Current US City Temperature

Fetches the latest observed temperature for a United States city using the free
Open-Meteo geocoding API and the National Weather Service (api.weather.gov), local 2
neither of which requires an API key.

When this skill is activated:

1. Determine the target US city from the user's request. If the name is ambiguous (e.g. "Springfield", "Portland", "Columbus"), ask the user for the state before continuing.
2. Run the helper script [Get-UsTemperature.ps1](./scripts/Get-UsTemperature.ps1), passing the city and the state when the user provided one, to look up the coordinates and fetch the latest station observation.
3. Report the current temperature in Fahrenheit in a short, direct answer. Include Celsius in parentheses when useful.

## Guidelines

- Only supports locations within the United States because the National Weather Service API does not cover places outside the US.
- Always show the temperature in Fahrenheit for US users; you may add Celsius in parentheses.
- Never fabricate temperature values. If the script returns an error or no observation, say so plainly and suggest trying a nearby larger city.
- Pass the state whenever the user gives one or when the city name is shared by multiple states so the correct location is selected.
- If the user asks for broader weather details, answer with the temperature and explain that this skill is focused on temperature only.
- See [instruction.txt](./docs/instruction.txt) for the operational instructions packaged with this skill.

## Examples

**Example 1: Simple city lookup**
- User request: "What is the temperature in Seattle?"
- Expected behavior: Run the script for "Seattle", then reply e.g. "It is currently 62 F in Seattle, WA."

**Example 2: Ambiguous city name**
- User request: "How cold is it in Portland right now?"
- Expected behavior: Ask "Portland, Oregon or Portland, Maine?", then run the script with the confirmed state before answering.

**Example 3: Non-US city**
- User request: "What is the temperature in Paris?"
- Expected behavior: Explain that this skill only covers US cities and that Paris, France is not supported.

## Notes

- "Current" temperature comes from the latest station observation, which can lag real time by up to roughly one hour.
- Some stations occasionally report null values; the script automatically checks the next few nearby stations to find valid temperature data.
- Temperature is converted from the API's Celsius values.
- Run the script with, for example: `pwsh -File ./scripts/Get-UsTemperature.ps1 -City "Austin" -State "Texas"`.