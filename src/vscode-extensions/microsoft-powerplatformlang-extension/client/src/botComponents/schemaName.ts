/**
 * Regex to filter out unwanted characters from schema names.
 *
 * - Matches anything not alphanumeric, or not one of: '_', '-', '.', '{}', '!'
 *
 * @example
 * // Removes spaces and special characters
 * 'test name!'.replace(SCHEMA_NAME_REGEX, '')  // returns 'testname!'
 *
 * @type {RegExp}
 * @see CDS SolutionComponentExtension schema name validation rules.
 * 
 * @original Copilot Studio schema name utility.
 */

export const SCHEMA_NAME_SUFFIX_LENGTH = 6;
export const MAX_SCHEMA_NAME_LENGTH = 100;
export const MAX_COLLISIONS = 20;

// Regex to filter out unwanted characters from schema names.
const SCHEMA_NAME_REGEX = /[^A-Za-z_\-.{}!0-9]*/g;

// Generates a random alphanumeric ID (similar to nanoid).
export function randId(length: number): string {
  const chars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
  let result = '';
  const charsLength = chars.length;
  for (let i = 0; i < length; i++) {
    result += chars.charAt(Math.floor(Math.random() * charsLength));
  }
  return result;
}

// Sanitizes the schema name by removing invalid characters and trimming to max length.
const cleanSchemaName = (
  schemaName: string,
  { lengthReservation = 0 }: { lengthReservation?: number } = {}
) =>
  schemaName.replace(SCHEMA_NAME_REGEX, '').slice(
    0,
    MAX_SCHEMA_NAME_LENGTH - lengthReservation
  );

// Generates the base schema name for a component.
const generateBaseSchemaName = ({
  botSchemaPrefix,
  componentDisplayName,
  componentPrefix,
}: {
  botSchemaPrefix: string;
  componentDisplayName: string;
  componentPrefix: string;
}) => {
  const cleanComponentDisplayName = cleanSchemaName(componentDisplayName);
  return cleanSchemaName(
    `${botSchemaPrefix}.${componentPrefix}.${
      cleanComponentDisplayName !== '' ? cleanComponentDisplayName : randId(3)
    }`
  );
};

// Resolves schema name collisions by appending a suffix if needed.
const resolveSchemaNameCollisions = ({
  baseSchemaName,
  existingSchemaNames,
  alwaysAddCollisionSuffix = false,
  suffixGenerator = () => `_${randId(SCHEMA_NAME_SUFFIX_LENGTH)}`,
}: {
  baseSchemaName: string;
  existingSchemaNames: string[];
  alwaysAddCollisionSuffix?: boolean;
  suffixGenerator?: () => string;
}) => {
  const existingNames = new Set(existingSchemaNames.map(name => name.toLowerCase()));
  let schemaName = baseSchemaName;
  let collisionSuffix = alwaysAddCollisionSuffix ? suffixGenerator() : '';
  let remainingAttempts = MAX_COLLISIONS;
  let isCollision = true;

  while (isCollision && remainingAttempts > 0) {
    schemaName = cleanSchemaName(
      `${cleanSchemaName(baseSchemaName, { lengthReservation: collisionSuffix.length })}${collisionSuffix}`
    );
    isCollision = existingNames.has(schemaName.toLowerCase());
    if (isCollision) {
      collisionSuffix = suffixGenerator();
    }
    remainingAttempts--;
  }

  return cleanSchemaName(schemaName);
};

// Generates a valid, unique schema name for a bot component.
export const generateSchemaNameForBotComponents = ({
  botSchemaPrefix,
  componentPrefix,
  componentName: componentDisplayName,
  existingSchemaNames,
  alwaysAddCollisionSuffix = false,
}: {
  botSchemaPrefix: string;
  componentPrefix: string;
  componentName: string;
  existingSchemaNames: string[];
  alwaysAddCollisionSuffix?: boolean;
}) => {
  const baseSchemaName = generateBaseSchemaName({
    botSchemaPrefix,
    componentDisplayName,
    componentPrefix,
  });

  return resolveSchemaNameCollisions({
    baseSchemaName,
    existingSchemaNames,
    alwaysAddCollisionSuffix,
    suffixGenerator: () => `_${randId(3)}`,
  });
};

