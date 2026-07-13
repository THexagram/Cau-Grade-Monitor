const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { buildGuiCourses, calculateRequiredGpa, isIncludedGpaRow, loadConfig, writeJsonAtomic } = require('./monitor');

test('includes required and sports courses in the weighted GPA', () => {
  const result = calculateRequiredGpa([
    { name: 'Required', type: '必修', credit: '3', score: 'A' },
    { name: 'Sports', category: '体育类', credit: '1', score: 'B+' },
    { name: 'Elective', type: '选修', credit: '4', score: 'A+' }
  ]);

  assert.equal(result.formatted, '3.83');
  assert.equal(result.counted, 2);
  assert.equal(result.required, 2);
  assert.equal(result.credits, 4);
});

test('finds sports in raw cells and excludes non-required courses', () => {
  assert.equal(isIncludedGpaRow({ type: '非必修' }), false);
  assert.equal(isIncludedGpaRow({ raw: ['2025-2026', '体育类'] }), true);
});

test('supports required-only and all-course GPA scopes', () => {
  const rows = [
    { type: '必修', credit: '3', score: 'A' },
    { type: '体育类', credit: '1', score: 'B+' },
    { type: '选修', credit: '2', score: 'B' }
  ];

  const requiredOnly = calculateRequiredGpa(rows, 'required');
  assert.equal(requiredOnly.formatted, '4.00');
  assert.equal(requiredOnly.counted, 1);

  const allCourses = calculateRequiredGpa(rows, 'all');
  assert.equal(allCourses.formatted, '3.55');
  assert.equal(allCourses.counted, 3);
});

test('supports an arbitrary exact selection of course types', () => {
  const rows = [
    { type: '专业必修', credit: '3', score: 'A' },
    { type: '体育类', credit: '1', score: 'B+' },
    { type: '限选', credit: '2', score: 'B' }
  ];

  const selected = calculateRequiredGpa(rows, 'required_and_sports', ['体育类', '限选']);
  assert.equal(selected.formatted, '3.10');
  assert.equal(selected.counted, 2);

  const none = calculateRequiredGpa(rows, 'required_and_sports', []);
  assert.equal(none.gpa, null);
  assert.equal(none.counted, 0);
});

test('builds compact GUI course rows with the current GPA inclusion flag', () => {
  const courses = buildGuiCourses([
    { term: '2025-2026-1', code: 'PE001', name: '体育', credit: '1', score: 'A-', type: '体育类', raw: ['体育类'] },
    { term: '2025-2026-1', code: 'EL001', name: '选修', credit: '2', score: 'A', type: '选修' }
  ], 'required_and_sports');

  assert.deepEqual(courses, [
    { term: '2025-2026-1', code: 'PE001', name: '体育', credit: '1', score: 'A-', type: '体育类', includedInGpa: true },
    { term: '2025-2026-1', code: 'EL001', name: '选修', credit: '2', score: 'A', type: '选修', includedInGpa: false }
  ]);
});

test('atomically replaces the GUI snapshot file', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'cau-grade-monitor-'));
  const snapshotFile = path.join(directory, 'snapshot.json');
  try {
    writeJsonAtomic(snapshotFile, { courses: [{ name: 'first' }] });
    writeJsonAtomic(snapshotFile, { courses: [{ name: 'latest' }] });
    assert.deepEqual(JSON.parse(fs.readFileSync(snapshotFile, 'utf8')), { courses: [{ name: 'latest' }] });
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('loads and normalizes the saved multi-select type rule', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'cau-grade-config-'));
  const configFile = path.join(directory, 'config.json');
  try {
    fs.writeFileSync(configFile, JSON.stringify({
      gpa: { useSelectedTypes: true, selectedTypes: [' 体育类 ', '限选', '限选'] },
      browser: {},
      proxy: {},
      feishu: {}
    }), 'utf8');
    const config = loadConfig(configFile);
    assert.equal(config.gpa.useSelectedTypes, true);
    assert.deepEqual(config.gpa.selectedTypes, ['体育类', '限选']);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
