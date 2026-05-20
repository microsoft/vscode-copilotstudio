import * as assert from 'node:assert';
import { describe, test } from 'node:test';
import { Uri } from 'vscode';
import {
	signIn,
	switchToAccount,
	clearSignInCancellation,
	isSignInCancelled,
	onAccountChange,
	getPreferredTreeAccount,
	getAccessTokenByAccountId,
} from '../../clients/account';
import * as accountModule from '../../clients/account';
import { DefaultCoreServicesClusterCategory } from '../../constants';

describe('Account', () => {
	/**
	 * Verify switchToAccount returns false when account not found
	 * 
	 * When passing an accountId that doesn't exist in VS Code's account list,
	 * the function should return false without prompting the user.
	 * These are pure logic tests - no network/auth required.
	 */
	test('returns false when account ID does not exist', async () => {
		const nonExistentAccountId = 'non-existent-account-id-12345';
		const accountLabel = 'test@example.com';
		
		const result = await switchToAccount(
			nonExistentAccountId,
			accountLabel,
			DefaultCoreServicesClusterCategory
		);
		
		assert.strictEqual(result, false, 'Should return false for non-existent account');
	});

	test('returns false for empty account ID', async () => {
		const result = await switchToAccount(
			'',
			'Empty Account',
			DefaultCoreServicesClusterCategory
		);
		
		assert.strictEqual(result, false, 'Should return false for empty account ID');
	});

	test('returns false for malformed account ID', async () => {
		const result = await switchToAccount(
			'malformed-guid-not-valid',
			'Malformed Account',
			DefaultCoreServicesClusterCategory
		);
		
		assert.strictEqual(result, false, 'Should return false for malformed account ID');
	});

	test('signin command', async () => {
        let threwDialogError = false;

        try {
            await signIn(DefaultCoreServicesClusterCategory);
        } catch (e: any) {
            if (
                e.message?.includes("DialogService: refused to show dialog") &&
                e.message?.includes("The extension 'Copilot Studio' wants to sign in using Microsoft")
            ) {
                threwDialogError = true;
            } else {
                throw e; 
            }
        }

        assert.strictEqual(
            threwDialogError,
            true,
            "Expected signIn to attempt showing VS Code dialog"
        );
    });

	test('setPreferredTreeAccount is not exported', () => {
		assert.strictEqual(
			(accountModule as Record<string, unknown>).setPreferredTreeAccount,
			undefined,
			'setPreferredTreeAccount must remain unexported'
		);
		assert.strictEqual(
			typeof accountModule.getPreferredTreeAccount,
			'function',
			'getPreferredTreeAccount reader must remain exported'
		);
	});

	test('sticky-cancellation surface starts cleared and clear is idempotent', () => {
		clearSignInCancellation();
		assert.strictEqual(isSignInCancelled(), false, 'should start cleared');
		clearSignInCancellation();
		assert.strictEqual(isSignInCancelled(), false, 'clear should be idempotent');
		assert.strictEqual(
			getPreferredTreeAccount(),
			undefined,
			'preferredTreeAccount has no public setter; should be undefined'
		);
	});

	test('onAccountChange returns a Disposable and does not fire on registration', async () => {
		let fireCount = 0;
		const disposable = await onAccountChange(() => { fireCount++; });
		try {
			assert.strictEqual(typeof disposable.dispose, 'function', 'must return a Disposable');
			assert.strictEqual(fireCount, 0, 'must not invoke callback synchronously at registration');
		} finally {
			disposable.dispose();
			disposable.dispose();
		}
	});

	test('concurrent switchToAccount with bogus id both resolve false', async () => {
		const bogus = 'nonexistent-account-id-parallel-test';
		const [a, b] = await Promise.all([
			switchToAccount(bogus, 'a@example.com', DefaultCoreServicesClusterCategory),
			switchToAccount(bogus, 'b@example.com', DefaultCoreServicesClusterCategory),
		]);
		assert.strictEqual(a, false, 'first call should return false');
		assert.strictEqual(b, false, 'second call should return false');
	});

	test('getAccessTokenByAccountId with no accountId surfaces documented error when not signed in', async () => {
		const resource = Uri.parse('https://example.crm.dynamics.com/');
		let observedMessage: string | undefined;
		try {
			await getAccessTokenByAccountId(resource, undefined);
		} catch (e) {
			observedMessage = (e as Error).message;
		}
		assert.ok(observedMessage, 'expected getAccessTokenByAccountId(undefined) to throw');
		const isExpected =
			observedMessage.includes('No signed-in account available for this request') ||
			observedMessage.includes('DialogService: refused to show dialog') ||
			observedMessage.includes('User canceled sign in');
		assert.strictEqual(
			isExpected,
			true,
			`unexpected error from no-accountId path: ${observedMessage}`
		);
	});
});
