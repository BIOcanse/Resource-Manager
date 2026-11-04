import type { AiGatewayCredentialView } from "../types.ts";
import type { RequestClient } from "../frontendRuntime/request/RequestClient.ts";
import { currentSettingsText } from "../text.ts";
import {
  defineResponseDecoder, requireArray, requireNonEmptyString,
  requireOneOf, requireRecord, requireString, ResponseDecodeError
} from "../frontendRuntime/request/ResponseDecoder.ts";

export const aiGatewayCredentialsDecoder = defineResponseDecoder<AiGatewayCredentialView[]>(
  "ai-gateway.credentials.v1",
  value => requireArray(value, "$").map((item, index) => {
    const path = `$[${index}]`;
    const row = requireRecord(item, path);
    const createdAt = requireString(row.createdAt, `${path}.createdAt`);
    if (!Number.isFinite(Date.parse(createdAt))) {
      throw new ResponseDecodeError(`${path}.createdAt`, "timestamp");
    }
    return {
      id: requireNonEmptyString(row.id, `${path}.id`),
      displayName: requireString(row.displayName, `${path}.displayName`),
      compatibilityProfile: requireOneOf(row.compatibilityProfile,
        `${path}.compatibilityProfile`, ["openai", "anthropic"] as const),
      createdAt,
      baseUrl: requireString(row.baseUrl, `${path}.baseUrl`),
      authenticationHeader: requireString(row.authenticationHeader, `${path}.authenticationHeader`)
    };
  }));

export function getAiGatewayCredentials(
  client: Pick<RequestClient, "request">,
  signal: AbortSignal
): Promise<AiGatewayCredentialView[]> {
  return client.request({
    key: "ai-gateway.credentials",
    url: "/api/ai-gateway/credentials",
    fallbackError: currentSettingsText().systemIntegration.aiGatewayLoadFailed,
    decoder: aiGatewayCredentialsDecoder,
    signal,
    request: { method: "GET" }
  });
}
