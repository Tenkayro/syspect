#!/usr/bin/env node
/**
 * Builds Syspect for Linux (amd64 + arm64) on the local machine and publishes
 * the resulting archives as a GitHub release.
 *
 * Usage:
 *   npm run release
 *   npm run release -- --tag v0.1.0 --yes
 *   npm run release -- --dry-run
 */

import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as p from '@clack/prompts';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const TARGETS = ['amd64', 'arm64'];
const SEMVER = /^v(\d+)\.(\d+)\.(\d+)$/;

function printHelp() {
	console.log(`Usage: npm run release -- [options]

Options:
  --tag <vX.Y.Z>   Release this version instead of prompting for one
  --yes, -y        Skip the confirmation prompt
  --dry-run        Build the archives but do not publish a release
  --allow-dirty    Allow a dirty working tree (not recommended)
  -h, --help       Show this help`);
}

function parseArgs(argv) {
	const options = { tag: null, yes: false, dryRun: false, allowDirty: false };
	for (let i = 0; i < argv.length; i++) {
		const arg = argv[i];
		if (arg === '--tag') options.tag = argv[++i] ?? null;
		else if (arg.startsWith('--tag=')) options.tag = arg.slice('--tag='.length);
		else if (arg === '--yes' || arg === '-y') options.yes = true;
		else if (arg === '--dry-run') options.dryRun = true;
		else if (arg === '--allow-dirty') options.allowDirty = true;
		else if (arg === '--help' || arg === '-h') {
			printHelp();
			process.exit(0);
		} else {
			throw new Error(`Unknown argument: ${arg} (use --help for usage)`);
		}
	}
	return options;
}

function exec(command, args, options = {}) {
	return execFileSync(command, args, {
		cwd: ROOT,
		encoding: 'utf8',
		stdio: ['ignore', 'pipe', 'pipe'],
		...options,
	}).trim();
}

function attempt(command, args, options) {
	try {
		return { ok: true, output: exec(command, args, options) };
	} catch (error) {
		return {
			ok: false,
			output: String(error.stdout ?? '').trim(),
			error: String(error.stderr ?? error.message ?? '').trim(),
		};
	}
}

function ensure(value) {
	if (p.isCancel(value)) {
		p.cancel('Release cancelled.');
		process.exit(0);
	}
	return value;
}

function normalizeTag(input) {
	const trimmed = String(input).trim();
	return trimmed.startsWith('v') ? trimmed : `v${trimmed}`;
}

function bumpVersion(latest, kind) {
	const base = SEMVER.exec(latest ?? 'v0.0.0');
	if (!base) throw new Error(`Cannot parse version: ${latest}`);
	const [, major, minor, patch] = base.map(Number);
	if (kind === 'major') return `v${major + 1}.0.0`;
	if (kind === 'minor') return `v${major}.${minor + 1}.0`;
	return `v${major}.${minor}.${patch + 1}`;
}

function formatSize(bytes) {
	const mb = bytes / (1024 * 1024);
	return `${mb.toFixed(1)} MB`;
}

function checkPrerequisites() {
	if (!attempt('git', ['rev-parse', '--is-inside-work-tree']).ok) {
		throw new Error('This script must run inside the Syspect git repository.');
	}

	const go = attempt('go', ['version']);
	if (!go.ok) throw new Error('Go is not installed or not in PATH.');
	const version = /go(\d+)\.(\d+)/.exec(go.output);
	if (version && Number(version[1]) === 1 && Number(version[2]) < 23) {
		throw new Error(`Go 1.23 or newer is required (found: ${go.output}).`);
	}

	if (!attempt('gh', ['auth', 'status']).ok) {
		throw new Error('GitHub CLI is not authenticated. Run "gh auth login" first.');
	}

	if (!attempt('tar', ['--version']).ok) {
		throw new Error('tar is required to create the release archives.');
	}
}

function latestTag() {
	const result = attempt('git', ['describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*']);
	if (result.ok && SEMVER.test(result.output)) return result.output;
	return null;
}

function assertTagIsFree(tag) {
	if (attempt('git', ['rev-parse', '-q', '--verify', `refs/tags/${tag}`]).ok) {
		throw new Error(`Tag ${tag} already exists locally.`);
	}
	if (exec('git', ['ls-remote', '--tags', 'origin', `refs/tags/${tag}`])) {
		throw new Error(`Tag ${tag} already exists on origin.`);
	}
}

function buildArchives(spinner) {
	const distDir = path.join(ROOT, 'dist');
	mkdirSync(distDir, { recursive: true });
	const assets = [];

	for (const arch of TARGETS) {
		const workDir = mkdtempSync(path.join(tmpdir(), `syspect-${arch}-`));
		try {
			const binary = path.join(workDir, 'syspect');
			spinner.start(`Building linux/${arch}`);

			const build = spawnSync(
				'go',
				['build', '-trimpath', '-ldflags=-s -w', '-o', binary, './cmd/syspect'],
				{
					cwd: ROOT,
					encoding: 'utf8',
					env: { ...process.env, GOOS: 'linux', GOARCH: arch, CGO_ENABLED: '0' },
				},
			);
			if (build.status !== 0) {
				spinner.stop(`Build failed for linux/${arch}`, 1);
				throw new Error(build.stderr?.trim() || 'go build failed.');
			}

			const asset = path.join(distDir, `syspect-linux-${arch}.tar.gz`);
			const archive = spawnSync('tar', ['-czf', asset, '-C', workDir, 'syspect'], {
				cwd: ROOT,
				encoding: 'utf8',
			});
			if (archive.status !== 0) {
				spinner.stop(`Archive failed for linux/${arch}`, 1);
				throw new Error(archive.stderr?.trim() || 'tar failed.');
			}

			const entries = exec('tar', ['-tzf', asset]).split(/\r?\n/).filter(Boolean);
			if (!entries.includes('syspect')) {
				spinner.stop(`Invalid archive for linux/${arch}`, 1);
				throw new Error(`Archive ${path.basename(asset)} does not contain the syspect executable.`);
			}

			const { size } = statSync(asset);
			spinner.stop(`Built ${path.basename(asset)} (${formatSize(size)})`);
			assets.push(asset);
		} finally {
			rmSync(workDir, { recursive: true, force: true });
		}
	}

	return assets;
}

async function main() {
	const options = parseArgs(process.argv.slice(2));

	p.intro('Syspect release');

	const spinner = p.spinner();
	let sha;
	spinner.start('Preparing');
	try {
		checkPrerequisites();

		const status = exec('git', ['status', '--porcelain']);
		if (status && !options.allowDirty) {
			throw new Error(
				'Working tree is not clean. Commit or stash your changes first (or pass --allow-dirty).',
			);
		}

		const fetched = attempt('git', ['fetch', '--tags', '--prune', 'origin']);
		if (!fetched.ok) {
			throw new Error(`Could not fetch from origin.\n${fetched.error}`);
		}

		sha = exec('git', ['rev-parse', 'HEAD']);
		if (!attempt('git', ['merge-base', '--is-ancestor', sha, 'origin/main']).ok) {
			throw new Error(
				'HEAD is not part of origin/main. Push your commits first so the tag points to a commit that exists on GitHub.',
			);
		}

		spinner.stop('Prerequisites ok');
	} catch (error) {
		spinner.stop('Prerequisites failed', 1);
		throw error;
	}

	const latest = latestTag();
	const base = latest ?? 'v0.0.0';

	let tag;
	if (options.tag) {
		tag = normalizeTag(options.tag);
		if (!SEMVER.test(tag)) throw new Error(`Invalid version "${options.tag}". Use vX.Y.Z.`);
	} else {
		const custom = '__custom__';
		const choice = ensure(
			await p.select({
				message: latest ? `Select the release version (latest: ${latest})` : 'Select the release version',
				initialValue: latest ? 'patch' : 'minor',
				options: [
					{ value: 'patch', label: `Patch  ${bumpVersion(base, 'patch')}` },
					{
						value: 'minor',
						label: `Minor  ${bumpVersion(base, 'minor')}${latest ? '' : '  (first release)'}`,
					},
					{ value: 'major', label: `Major  ${bumpVersion(base, 'major')}` },
					{ value: custom, label: 'Custom version' },
				],
			}),
		);

		if (choice === custom) {
			const input = ensure(
				await p.text({
					message: 'Version',
					placeholder: 'v0.1.0',
					validate: (value) =>
						SEMVER.test(normalizeTag(value ?? '')) ? undefined : 'Use a semver version such as v0.1.0',
				}),
			);
			tag = normalizeTag(input);
		} else {
			tag = bumpVersion(base, choice);
		}
	}

	assertTagIsFree(tag);

	const subject = exec('git', ['log', '-1', '--pretty=%s']);
	p.note(
		[
			`Tag:     ${tag}`,
			`Commit:  ${sha.slice(0, 7)} ${subject}`,
			'Assets:  syspect-linux-amd64.tar.gz',
			'         syspect-linux-arm64.tar.gz',
			'Publish: gh release create (local build, no CI)',
		].join('\n'),
		'Release plan',
	);

	if (!options.yes) {
		const confirmed = ensure(
			await p.confirm({ message: `Build and publish ${tag}?`, initialValue: true }),
		);
		if (!confirmed) {
			p.cancel('Release cancelled.');
			process.exit(0);
		}
	}

	let assets;
	try {
		assets = buildArchives(spinner);
	} catch (error) {
		throw new Error(`Build failed.\n${error.message}`);
	}

	if (options.dryRun) {
		p.note(assets.map((asset) => path.relative(ROOT, asset)).join('\n'), 'Dry run artifacts');
		p.outro('Dry run finished. Nothing was published.');
		return;
	}

	spinner.start(`Publishing ${tag}`);
	const release = spawnSync(
		'gh',
		['release', 'create', tag, '--target', sha, '--title', tag, '--generate-notes', ...assets],
		{ cwd: ROOT, encoding: 'utf8' },
	);
	if (release.status !== 0) {
		spinner.stop(`Publishing ${tag} failed`, 1);
		throw new Error(release.stderr?.trim() || 'gh release create failed.');
	}
	spinner.stop(`Published ${tag}`);

	attempt('git', ['fetch', '--tags', '--force', '--quiet', 'origin']);

	const url = release.stdout?.trim().split(/\r?\n/).find((line) => line.startsWith('http'));
	p.note(
		'The tag was created through the GitHub API, so the tag-push workflow does not run. The local build is the released artifact.',
		'Note',
	);
	p.outro(url ? `Done: ${url}` : `Done: ${tag} published.`);
}

try {
	await main();
} catch (error) {
	p.log.error(error instanceof Error ? error.message : String(error));
	p.outro('Release aborted.');
	process.exit(1);
}
