const test = require('node:test');
const assert = require('node:assert/strict');
const { calculateRequiredGpa, isIncludedGpaRow } = require('./monitor');

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
