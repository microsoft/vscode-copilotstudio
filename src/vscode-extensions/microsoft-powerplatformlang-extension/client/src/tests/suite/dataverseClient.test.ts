import * as assert from 'assert';

// Import the module to test
import { clearWhoAmICache, parseBatchAccessResults } from '../../clients/dataverseClient';

suite('clearWhoAmICache', () => {
	/**
	 * A1: Verify clearWhoAmICache is callable and idempotent
	 * 
	 * This is a lightweight sanity check that the function exists and doesn't throw.
	 * Full cache behavior testing would require mocking the network layer.
	 */
	test('clearWhoAmICache can be called multiple times without error', () => {
		// Should not throw when called on empty caches
		assert.doesNotThrow(() => clearWhoAmICache());
		
		// Should be idempotent - safe to call multiple times
		assert.doesNotThrow(() => clearWhoAmICache());
		assert.doesNotThrow(() => clearWhoAmICache());
	});
});

suite('parseBatchAccessResults', () => {
	/**
	 * Helper to create a multipart batch response part
	 */
	function createBatchPart(contentId: number, httpStatus: number, accessRights: string | null): string {
		const statusText = httpStatus === 200 ? 'OK' : httpStatus === 403 ? 'Forbidden' : 'Not Found';
		const body = accessRights !== null
			? `{"@odata.context":"...","AccessRights":"${accessRights}"}`
			: `{"error":{"code":"0x80040220","message":"Access denied"}}`;
		
		return [
			`Content-Type: application/http`,
			`Content-Transfer-Encoding: binary`,
			``,
			`HTTP/1.1 ${httpStatus} ${statusText}`,
			`Content-ID: ${contentId}`,
			`Content-Type: application/json; odata.metadata=minimal`,
			``,
			body
		].join('\r\n');
	}

	/**
	 * Helper to wrap parts in a batch response with boundary markers
	 */
	function createBatchResponse(parts: string[]): string {
		const boundary = 'batchresponse_abc123';
		return parts.map(part => `--${boundary}\r\n${part}`).join('\r\n') + `\r\n--${boundary}--`;
	}

	test('parses normal multipart response with multiple AccessRights', () => {
		const response = createBatchResponse([
			createBatchPart(0, 200, 'ReadAccess, WriteAccess, AppendAccess'),
			createBatchPart(1, 200, 'ReadAccess'),
			createBatchPart(2, 200, 'ReadAccess, WriteAccess'),
		]);

		const results = parseBatchAccessResults(response, 3);

		assert.deepStrictEqual(results, [true, false, true]);
	});

	test('handles response with one 403 error and others succeeding', () => {
		const response = createBatchResponse([
			createBatchPart(0, 200, 'ReadAccess, WriteAccess'),
			createBatchPart(1, 403, null),
			createBatchPart(2, 200, 'ReadAccess, WriteAccess'),
		]);

		const results = parseBatchAccessResults(response, 3);

		// Bot 1 should be false due to 403, others should be true
		assert.deepStrictEqual(results, [true, false, true]);
	});

	test('handles response with out-of-order Content-IDs', () => {
		// Response arrives in different order than request
		const response = createBatchResponse([
			createBatchPart(2, 200, 'ReadAccess, WriteAccess'),
			createBatchPart(0, 200, 'ReadAccess'),
			createBatchPart(1, 200, 'ReadAccess, WriteAccess'),
		]);

		const results = parseBatchAccessResults(response, 3);

		// Should correctly correlate by Content-ID, not by position
		assert.deepStrictEqual(results, [false, true, true]);
	});

	test('handles empty response', () => {
		const results = parseBatchAccessResults('', 3);

		// All should default to false
		assert.deepStrictEqual(results, [false, false, false]);
	});

	test('handles malformed response with no Content-ID', () => {
		const malformedResponse = [
			`--batchresponse_abc123`,
			`Content-Type: application/http`,
			``,
			`HTTP/1.1 200 OK`,
			``, // Missing Content-ID header
			`{"AccessRights":"ReadAccess, WriteAccess"}`,
			`--batchresponse_abc123--`
		].join('\r\n');

		const results = parseBatchAccessResults(malformedResponse, 2);

		// Should default to false since Content-ID is missing
		assert.deepStrictEqual(results, [false, false]);
	});

	test('handles response with 404 errors', () => {
		const response = createBatchResponse([
			createBatchPart(0, 200, 'ReadAccess, WriteAccess'),
			createBatchPart(1, 404, null),
		]);

		const results = parseBatchAccessResults(response, 2);

		assert.deepStrictEqual(results, [true, false]);
	});

	test('handles Content-ID with angle brackets', () => {
		// Some servers return Content-ID: <0> instead of Content-ID: 0
		const response = [
			`--batchresponse_abc123`,
			`Content-Type: application/http`,
			`Content-Transfer-Encoding: binary`,
			``,
			`HTTP/1.1 200 OK`,
			`Content-ID: <0>`,
			`Content-Type: application/json`,
			``,
			`{"AccessRights":"ReadAccess, WriteAccess"}`,
			`--batchresponse_abc123--`
		].join('\r\n');

		const results = parseBatchAccessResults(response, 1);

		assert.deepStrictEqual(results, [true]);
	});

	test('returns all false for expectedCount of 0', () => {
		const results = parseBatchAccessResults('any content', 0);

		assert.deepStrictEqual(results, []);
	});

	test('ignores Content-IDs outside expected range', () => {
		const response = createBatchResponse([
			createBatchPart(0, 200, 'ReadAccess, WriteAccess'),
			createBatchPart(5, 200, 'ReadAccess, WriteAccess'), // Out of range for expectedCount=2
		]);

		const results = parseBatchAccessResults(response, 2);

		// Only Content-ID 0 should be processed; index 1 remains false
		assert.deepStrictEqual(results, [true, false]);
	});

	test('handles mixed success and various error codes', () => {
		const response = createBatchResponse([
			createBatchPart(0, 200, 'ReadAccess'),
			createBatchPart(1, 200, 'WriteAccess'),
			createBatchPart(2, 403, null),
			createBatchPart(3, 404, null),
			createBatchPart(4, 200, 'ReadAccess, WriteAccess, DeleteAccess'),
		]);

		const results = parseBatchAccessResults(response, 5);

		assert.deepStrictEqual(results, [false, true, false, false, true]);
	});
});
