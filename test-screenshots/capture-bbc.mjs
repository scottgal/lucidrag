// Capture BBC News screenshots for OCR testing
import puppeteer from 'puppeteer';
import { mkdir } from 'fs/promises';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';
import { setTimeout } from 'timers/promises';

const __dirname = dirname(fileURLToPath(import.meta.url));

async function captureScreenshots() {
    const outputDir = __dirname;
    await mkdir(outputDir, { recursive: true });

    console.log('Launching browser...');
    const browser = await puppeteer.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox']
    });

    const page = await browser.newPage();
    await page.setViewport({ width: 1920, height: 1080 });

    const screenshots = [
        {
            name: 'bbc-news-headline',
            url: 'https://www.bbc.com/news',
            description: 'Full news page with headlines'
        },
        {
            name: 'bbc-sport',
            url: 'https://www.bbc.com/sport',
            description: 'Sports page with mixed layout'
        },
        {
            name: 'wikipedia-article',
            url: 'https://en.wikipedia.org/wiki/Optical_character_recognition',
            description: 'Dense encyclopedic text'
        }
    ];

    for (const shot of screenshots) {
        try {
            console.log(`Capturing: ${shot.name} from ${shot.url}`);
            await page.goto(shot.url, { waitUntil: 'networkidle2', timeout: 30000 });

            // Wait for content to load
            await setTimeout(2000);

            const path = join(outputDir, `${shot.name}.png`);
            await page.screenshot({ path, fullPage: false });

            console.log(`  Saved: ${path}`);
        } catch (err) {
            console.error(`  Failed: ${shot.name} - ${err.message}`);
        }
    }

    await browser.close();
    console.log('Done!');
}

captureScreenshots().catch(console.error);
