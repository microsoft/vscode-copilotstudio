import * as assert from 'assert';
import { signIn, switchToAccount } from '../../clients/account';
import { DefaultCoreServicesClusterCategory } from '../../constants';

suite('Account', () => {
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
});
