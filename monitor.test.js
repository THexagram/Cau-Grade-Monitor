const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { buildGuiCourses, calculateRequiredGpa, isIncludedGpaRow, loadConfig, reloadGpaConfigIfChanged, writeJsonAtomic } = require('./monitor');

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

test('applies per-course include and exclude exceptions on top of the type rule', () => {
  const required = { term: '2025-2026-1', code: 'R001', name: '必修课', type: '必修', credit: '3', score: 'A' };
  const elective = { term: '2025-2026-1', code: 'E001', name: '选修课', type: '选修', credit: '2', score: 'B' };
  const rows = [required, elective];

  const withElective = calculateRequiredGpa(rows, 'required_and_sports', null, {
    included: ['2025-2026-1||E001||选修课||2'],
    excluded: []
  });
  assert.equal(withElective.formatted, '3.60');
  assert.equal(withElective.counted, 2);

  const electiveOnly = calculateRequiredGpa(rows, 'required_and_sports', null, {
    included: ['2025-2026-1||E001||选修课||2'],
    excluded: ['2025-2026-1||R001||必修课||3']
  });
  assert.equal(electiveOnly.formatted, '3.00');
  assert.equal(electiveOnly.counted, 1);
});

test('builds compact GUI course rows with the current GPA inclusion flag', () => {
  const courses = buildGuiCourses([
    { term: '2025-2026-1', code: 'PE001', name: '体育', credit: '1', score: 'A-', type: '体育类', raw: ['体育类'] },
    { term: '2025-2026-1', code: 'EL001', name: '选修', credit: '2', score: 'A', type: '选修' }
  ], 'required_and_sports');

  assert.deepEqual(courses, [
    { key: '2025-2026-1||PE001||体育||1', term: '2025-2026-1', code: 'PE001', name: '体育', credit: '1', score: 'A-', type: '体育类', baseIncludedInGpa: true, includedInGpa: true, gpaEligible: true },
    { key: '2025-2026-1||EL001||选修||2', term: '2025-2026-1', code: 'EL001', name: '选修', credit: '2', score: 'A', type: '选修', baseIncludedInGpa: false, includedInGpa: false, gpaEligible: true }
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
      gpa: {
        useSelectedTypes: true,
        selectedTypes: [' 体育类 ', '限选', '限选'],
        includedCourseKeys: [' course-a ', 'course-a'],
        excludedCourseKeys: ['course-b', ' course-b ']
      },
      browser: {},
      proxy: {},
      feishu: {}
    }), 'utf8');
    const config = loadConfig(configFile);
    assert.equal(config.gpa.useSelectedTypes, true);
    assert.deepEqual(config.gpa.selectedTypes, ['体育类', '限选']);
    assert.deepEqual(config.gpa.includedCourseKeys, ['course-a']);
    assert.deepEqual(config.gpa.excludedCourseKeys, ['course-b']);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('hot reloads changed GPA rules without replacing the running config', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'cau-grade-hot-reload-'));
  const configFile = path.join(directory, 'config.json');
  try {
    fs.writeFileSync(configFile, JSON.stringify({
      gpa: { scope: 'required_and_sports', useSelectedTypes: false },
      browser: {},
      proxy: {},
      feishu: {}
    }), 'utf8');
    const config = loadConfig(configFile);

    fs.writeFileSync(configFile, JSON.stringify({
      gpa: {
        scope: 'required_and_sports',
        useSelectedTypes: true,
        selectedTypes: ['限选'],
        includedCourseKeys: ['course-a'],
        excludedCourseKeys: ['course-b']
      }
    }), 'utf8');
    const future = new Date(Date.now() + 2000);
    fs.utimesSync(configFile, future, future);

    assert.equal(reloadGpaConfigIfChanged(config), true);
    assert.equal(config.gpa.useSelectedTypes, true);
    assert.deepEqual(config.gpa.selectedTypes, ['限选']);
    assert.deepEqual(config.gpa.includedCourseKeys, ['course-a']);
    assert.deepEqual(config.gpa.excludedCourseKeys, ['course-b']);
    assert.equal(reloadGpaConfigIfChanged(config), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
