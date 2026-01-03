const puppeteer = require('puppeteer');
const path = require('path');
const fs = require('fs');

const BASE_URL = 'http://localhost:5080';
const SCREENSHOT_DIR = path.join(__dirname, 'screenshots');

async function takeScreenshots() {
    if (!fs.existsSync(SCREENSHOT_DIR)) {
        fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
    }

    console.log('Launching browser...');
    const browser = await puppeteer.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox'],
        protocolTimeout: 60000
    });

    const page = await browser.newPage();
    await page.setViewport({ width: 1920, height: 1080 });

    try {
        // Screenshot 1: Home page
        console.log('1. Taking home page screenshot...');
        await page.goto(BASE_URL, { waitUntil: 'networkidle0', timeout: 30000 });
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, '01-home.png'), fullPage: true });

        // Screenshot 2: Type a question - set Alpine.js data directly
        console.log('2. Typing question...');
        await page.evaluate(() => {
            // Find Alpine component and set currentMessage
            const alpineRoot = document.querySelector('[x-data]');
            if (alpineRoot && alpineRoot._x_dataStack) {
                const data = alpineRoot._x_dataStack[0];
                data.currentMessage = 'What is HTMX and how does it work with ASP.NET Core?';
            }
            // Also set input value as backup
            const input = document.querySelector('input[type="text"]');
            if (input) {
                input.value = 'What is HTMX and how does it work with ASP.NET Core?';
                input.dispatchEvent(new Event('input', { bubbles: true }));
            }
        });
        await new Promise(r => setTimeout(r, 1000));
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, '02-question-typed.png'), fullPage: true });

        // Submit by calling the sendMessage function directly
        console.log('3. Submitting question...');
        await page.evaluate(() => {
            const alpineRoot = document.querySelector('[x-data]');
            if (alpineRoot && alpineRoot._x_dataStack) {
                const data = alpineRoot._x_dataStack[0];
                data.sendMessage();
            }
        });
        await new Promise(r => setTimeout(r, 10000)); // Wait for API response
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, '03-chat-response.png'), fullPage: true });

        // Screenshot 4: Evidence view - click button containing "Evidence" text
        console.log('4. Switching to Evidence view...');
        await page.evaluate(() => {
            const buttons = [...document.querySelectorAll('.btn-group button')];
            const evidenceBtn = buttons.find(b => b.textContent.trim() === 'Evidence');
            if (evidenceBtn) evidenceBtn.click();
        });
        await new Promise(r => setTimeout(r, 500));
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, '04-evidence-view.png'), fullPage: true });

        // Screenshot 5: Graph view
        console.log('5. Switching to Graph view...');
        await page.evaluate(() => {
            const buttons = [...document.querySelectorAll('.btn-group button')];
            const graphBtn = buttons.find(b => b.textContent.trim() === 'Graph');
            if (graphBtn) graphBtn.click();
        });
        await new Promise(r => setTimeout(r, 500));
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, '05-graph-view.png'), fullPage: true });

        // Screenshot 6: Back to Answer view
        console.log('6. Back to Answer view...');
        await page.evaluate(() => {
            const buttons = [...document.querySelectorAll('.btn-group button')];
            const answerBtn = buttons.find(b => b.textContent.trim() === 'Answer');
            if (answerBtn) answerBtn.click();
        });
        await new Promise(r => setTimeout(r, 500));
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, '06-answer-view.png'), fullPage: true });

        console.log('\nAll screenshots saved to:', SCREENSHOT_DIR);

    } catch (error) {
        console.error('Error:', error.message);
        await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'error-state.png'), fullPage: true });
    } finally {
        await browser.close();
    }
}

takeScreenshots().catch(console.error);
