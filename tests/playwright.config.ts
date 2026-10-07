import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',

  /* Run tests in files in parallel */
  fullyParallel: true,

  /* Fail the build on CI if test.only is accidentally left in the source */
  forbidOnly: !!process.env.CI,

  /* Retry failed tests on CI */
  retries: process.env.CI ? 2 : 0,

  /* Use one worker on CI */
  workers: process.env.CI ? 1 : undefined,

  /* HTML test report */
  reporter: 'html',

  /* Shared settings for all tests */
  use: {
    /* Merkai CRM URL */
    baseURL: 'https://localhost:5093',

    /* Ignore local HTTPS certificate errors */
    ignoreHTTPSErrors: true,

    /* Capture trace when a test is retried */
    trace: 'on-first-retry',

    /* Take screenshot when test fails */
    screenshot: 'only-on-failure',

    /* Record video when test fails */
    video: 'retain-on-failure',
  },

  /* Start with Chromium */
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});