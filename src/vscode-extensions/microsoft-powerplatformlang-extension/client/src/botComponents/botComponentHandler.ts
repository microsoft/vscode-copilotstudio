import * as https from 'https';
import logger from '../services/logger';
import { TelemetryEventsKeys } from '../constants';
import { AgentSyncInfo } from '../types';

export interface WsComponentMetadata {
  id: string;
  schemaName: string;
  modifiedOn: number;     // UTC millis
  sizeInBytes?: number;
  filename?: string;  
  agentId: string;
  agentSchemaName: string;
}

export interface HttpResponse {
  statusCode: number;
  headers: Record<string, string | string[] | undefined>;
  body: Buffer;
}

export class botComponentHandler {
  private baseUrl: string;
  private accessToken: string;

  constructor(baseUrl: string, accessToken: string) {
    this.baseUrl = baseUrl;
    this.accessToken = accessToken;
  }

  public async dataverseHttpRequest({url, method, body, extraHeaders = {}}: {url: URL; method: string; body?: string | Buffer; extraHeaders?: Record<string, string>; }): Promise<HttpResponse> {
    return new Promise((resolve, reject) => {
      const req = https.request(url, {
        method,
        headers: {
          Authorization: `Bearer ${this.accessToken}`,
          Accept: '*/*',
          ...extraHeaders
        }
      }, (res) => {
        const chunks: Buffer[] = [];

        res.on('data', (chunk) => {
          chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
        });

        res.on('end', () => {
          resolve({
            statusCode: res.statusCode || 0,
            headers: res.headers,
            body: Buffer.concat(chunks)
          });
        });
      });

      req.on('error', (err) => {
        reject(err);
      });

      if (body) {
        req.write(body);
      }
      req.end();
    });
  }

  public async listWsComponentMetadata(syncInfo: AgentSyncInfo): Promise<WsComponentMetadata[]> {    
    const botPrefix = await this.getBotPrefix(syncInfo.agentId);    
    const childAgents = await this.getChildAgents(syncInfo, botPrefix);
    const allAgentIds = [syncInfo.agentId, ...childAgents.map(c => c.id)];
      
    const query = [
      `$select=botcomponentid,schemaname,modifiedon,_parentbotcomponentid_value`,
      `$filter=startswith(schemaname,'${botPrefix}')`,
      `$expand=botcomponent_FileAttachments($select=filesizeinbytes,filename)`
    ].join('&');

    const url = new URL(`/api/data/v9.2/botcomponents?${query}`, this.baseUrl);
    const res = await this.dataverseHttpRequest({ url, method: 'GET' });

    if (res.statusCode >= 400) {
      logger.logError(TelemetryEventsKeys.DownloadKnowledgeFileError, undefined, { message: `Failed to list metadata: ${res.statusCode} - ${res.body}` });
      throw new Error(`Failed to list filtered metadata ${res.statusCode} - ${res.body}`);
    }

    const body = this.parseJson(res.body);
    return (body.value || []).map((rec: any) => {
      const first = (rec.botcomponent_FileAttachments || [])[0];
      return {
        id: rec.botcomponentid,
        schemaName: rec.schemaname,
        modifiedOn: new Date(rec.modifiedon).getTime(),
        sizeInBytes: first?.filesizeinbytes,
        filename: first?.filename,
        agentId: allAgentIds.includes(rec._parentbotcomponentid_value) ? rec._parentbotcomponentid_value : syncInfo.agentId,
        agentSchemaName: allAgentIds.includes(rec._parentbotcomponentid_value) ? childAgents.find(c => c.id === rec._parentbotcomponentid_value)?.schemaName : botPrefix
      };
    });
  }
  
  public async getChildAgents(syncInfo: AgentSyncInfo, botPrefix: string): Promise<{ id: string; schemaName: string; modifiedOn: number }[]> {
    const getChildQuery = [
      `$select=botcomponentid,schemaname,modifiedon`,
      `$filter=startswith(schemaname,'${botPrefix}.agent.')`
    ].join('&');

    const getChildUrl = new URL(`/api/data/v9.2/botcomponents?${getChildQuery}`, this.baseUrl);
    const getChildResponse = await this.dataverseHttpRequest({ url: getChildUrl, method: 'GET' });

    const childAgents: { id: string; schemaName: string; modifiedOn: number }[] = [];
    if (getChildResponse.statusCode === 200) {
      const childBody = this.parseJson(getChildResponse.body);
      childBody.value?.forEach((rec: any) => {
        childAgents.push({
          id: rec.botcomponentid,
          schemaName: rec.schemaname,
          modifiedOn: new Date(rec.modifiedon).getTime()
        });
      });
    }
    return childAgents;
  }

  public async getBotComponentId(name: string, schemaName: string, agentId: string): Promise<string> {
    const url = new URL(
      `/api/data/v9.2/botcomponents?$filter=schemaname eq '${schemaName}'&$select=botcomponentid`,
      this.baseUrl
    );
    const res = await this.dataverseHttpRequest({ url, method: 'GET' });
    const body = this.parseJson(res.body);

    if (body.value?.length) {
      return body.value[0].botcomponentid;
    }

    return this.createBotComponent(name, schemaName, agentId);
  }

  public async createBotComponent(name: string, schemaName: string, agentId: string, mainAgentId : string = agentId, isChildAgent: boolean = false, description?: string): Promise<string> {
    const data: any = {
      name,
      description: description ?? `Knowledge source for ${name}`,
      componenttype: 14,
      schemaname: schemaName
    };

    if (isChildAgent) {
      data['ParentBotComponentId@odata.bind'] = `/botcomponents(${agentId})`;
      data['parentbotid@odata.bind'] = `/bots(${mainAgentId})`;
    } else {
      data['parentbotid@odata.bind'] = `/bots(${agentId})`;
    }

    const postData = JSON.stringify(data);

    const url = new URL(`/api/data/v9.2/botcomponents`, this.baseUrl);
    const res = await this.dataverseHttpRequest({
      url,
      method: 'POST',
      body: postData,
      extraHeaders: { 'Content-Type': 'application/json' }
    });

    const parsed = this.parseJson(res.body);
    let locationHeader = res.headers.location;
    let idFromLocation = '';

    if (typeof locationHeader === 'string') {
      idFromLocation = locationHeader.match(/\(([^)]+)\)/)?.[1] ?? '';
    } else if (Array.isArray(locationHeader) && locationHeader.length > 0) {
      idFromLocation = locationHeader[0].match(/\(([^)]+)\)/)?.[1] ?? '';
    }

    return parsed?.botcomponentid ?? idFromLocation;
  }

  public async getBotPrefix(botId: string): Promise<string> {
    const botUrl = new URL(`/api/data/v9.2/bots(${botId})?$select=schemaname`, this.baseUrl);
    const botRes = await this.dataverseHttpRequest({ url: botUrl, method: 'GET' });

    if (botRes.statusCode === 200) {
        const botBody = this.parseJson(botRes.body);
        if (botBody.schemaname) {
          return botBody.schemaname;
        }
    }

    const errorMsg = `Failed to fetch bot schema for Bot ID ${botId}, status code: ${botRes.statusCode}`;
    logger.logError(TelemetryEventsKeys.GetBotPrefixError, undefined, { message: errorMsg});
    throw new Error(errorMsg);
  }

  public async downloadKnowledgeFile(botComponentId: string): Promise<Buffer> {
    const url = new URL(`/api/data/v9.2/botcomponents(${botComponentId})/filedata/$value`, this.baseUrl);
    const res = await this.dataverseHttpRequest({ url, method: 'GET' });

    if (res.statusCode >= 400) {
      logger.logError(TelemetryEventsKeys.DownloadKnowledgeFileError, undefined, { message: `Failed to download file: ${res.statusCode}` });
      throw new Error(`Failed to download knowledge file: ${res.statusCode}`);
    }

    return res.body;
  }

  public async updateBotComponentDescription(botComponentId: string, description: string): Promise<void> {
    const url = new URL(`/api/data/v9.2/botcomponents(${botComponentId})`, this.baseUrl);
    const body = JSON.stringify({ description });
    const res = await this.dataverseHttpRequest({
      url,
      method: 'PATCH',
      body,
      extraHeaders: { 'Content-Type': 'application/json' }
    });

    if (res.statusCode >= 400) {
      logger.logError(TelemetryEventsKeys.UploadKnowledgeFileError, undefined, { message: `Failed to update description for ${botComponentId}: ${res.statusCode}` });
    }
  }

  public async deleteBotComponent(botComponentId: string): Promise<void> {
    const url = new URL(`/api/data/v9.2/botcomponents(${botComponentId})`, this.baseUrl);
    const res = await this.dataverseHttpRequest({ url, method: 'DELETE' });

    if (res.statusCode >= 400) {
      logger.logError(TelemetryEventsKeys.DeleteBotComponentError, undefined, { message: `Failed to delete bot component: ${res.statusCode}` });
      throw new Error(`Failed to delete bot component: ${res.statusCode}`);
    }
  }

  private parseJson(buf: Buffer): any | null {
    const str = buf.toString('utf8').trim();
    if (!str) {
      return null;
    }
    try {
      return JSON.parse(str);
    } catch {
      return null;
    }
  }
}
