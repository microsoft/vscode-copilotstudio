import { Uri } from "vscode";
import { unescape } from "querystring";
import { AccountInfo } from "../types";
import { CoreServicesClusterCategory, DefaultCoreServicesClusterCategory } from "../constants";

export const getClusterCategory = (accountInfo?: Partial<AccountInfo>): CoreServicesClusterCategory => {
  return accountInfo?.clusterCategory || DefaultCoreServicesClusterCategory;
};

/**
 * Checks if childUri is a child of parentUri, handling encoding differences
 */
export const isChildUri = (childUri: string, parentUri: string): boolean => {
  // Normalize both URIs to ensure consistent comparison
  const normalizedChild = unescape(childUri.toLowerCase());
  const normalizedParent = unescape(parentUri.toLowerCase());
  return normalizedChild.startsWith(normalizedParent);
};

export const isSameUri = (left: Uri, right: Uri): boolean => {
  // Normalize both URIs to ensure consistent comparison
  const normalizedLeft = unescape(left.toString().toLowerCase());
  const normalizedRight = unescape(right.toString().toLowerCase());
  return normalizedLeft === normalizedRight;
};
