import * as assert from 'assert';
import { botComponentHandler, WsComponentMetadata } from '../../botComponents/botComponentHandler';
import logger from '../../services/logger';

suite('botComponentHandler', () => {
    const baseUrl = 'https://org1.crm.dynamics.com';
    const accessToken = 'access-token';
    let handler: botComponentHandler;

    setup(() => {
        handler = new botComponentHandler(baseUrl, accessToken);
    });

    test('getBotPrefix returns schemaName when statusCode 200', async () => {
        handler.dataverseHttpRequest = async () => ({
            statusCode: 200,
            headers: {},
            body: Buffer.from(JSON.stringify({ schemaname: 'myBotSchema' }))
        });

        const prefix = await handler.getBotPrefix('123');
        assert.strictEqual(prefix, 'myBotSchema');
    });

    test('getBotPrefix throws error when statusCode != 200', async () => {
        handler.dataverseHttpRequest = async () => ({
            statusCode: 404,
            headers: {},
            body: Buffer.from('{}')
        });

        let loggedError = '';
        (logger.logError as any) = (_key: string, _msg?: string, props?: any) => {
            loggedError = props?.message ?? '';
        };

        await assert.rejects(
            async () => {
                await handler.getBotPrefix('123');
            },
            /Failed to fetch bot schema for Bot ID 123/
        );

        assert.ok(loggedError.includes('Failed to fetch bot schema'));
    });

    test('getBotPrefix throws error when schemaname is missing', async () => {
        handler.dataverseHttpRequest = async () => ({
            statusCode: 200,
            headers: {},
            body: Buffer.from(JSON.stringify({}))
        });

        let loggedError = '';
        (logger.logError as any) = (_key: string, _msg?: string, props?: any) => {
            loggedError = props?.message ?? '';
        };

        await assert.rejects(
            async () => {
                await handler.getBotPrefix('123');
            },
            /Failed to fetch bot schema for Bot ID 123/
        );

        assert.ok(loggedError.includes('Failed to fetch bot schema'));
    });

    test('listWsComponentMetadata parses returned metadata', async () => {
        handler.getBotPrefix = async () => 'botPrefix';

        handler.dataverseHttpRequest = async () => ({
            statusCode: 200,
            headers: {},
            body: Buffer.from(JSON.stringify({
                value: [
                    {
                        botcomponentid: 'abc',
                        schemaname: 'botPrefix_d1c',
                        modifiedon: '2026-01-06T00:00:00Z',
                        botcomponent_FileAttachments: [
                            { filesizeinbytes: 123, filename: 'file.txt' }
                        ]
                    }
                ]
            }))
        });

        const metadata: WsComponentMetadata[] = await handler.listWsComponentMetadata({ agentId: '123' } as any);
        assert.strictEqual(metadata.length, 1);
        assert.strictEqual(metadata[0].id, 'abc');
        assert.strictEqual(metadata[0].schemaName, 'botPrefix_d1c');
        assert.strictEqual(metadata[0].sizeInBytes, 123);
        assert.strictEqual(metadata[0].filename, 'file.txt');
    });

    test('parseJson returns null for empty buffer', () => {
        const result = (handler as any).parseJson(Buffer.from(''));
        assert.strictEqual(result, null);
    });

    test('parseJson returns null for invalid JSON', () => {
        const result = (handler as any).parseJson(Buffer.from('invalid'));
        assert.strictEqual(result, null);
    });

    test('parseJson returns object for valid JSON', () => {
        const obj = { x: 1 };
        const result = (handler as any).parseJson(Buffer.from(JSON.stringify(obj)));
        assert.deepStrictEqual(result, obj);
    });
});
