import test from 'node:test';
import assert from 'node:assert/strict';
import {classifyVessel,projectHousekeeping} from '../science-housekeeping.mjs';
const id='12345678-1234-1234-1234-123456789012';
const preparedAt='2026-11-02T21:42:00.000Z',completedAt='2026-11-02T21:43:00.000Z';
const options={preparedAt,completedAt,createdAfter:new Set(),anyDocking:false};
const row={id,birthtimeMs:Date.parse(preparedAt)-3600000,mtimeMs:Date.parse(completedAt)+1000};
test('normal saving cannot turn an existing sandbox vessel into a new launch',()=>assert.equal(classifyVessel(row,options),'sandbox'));
test('post-cutover creations are preserved even with old/copied filesystem times',()=>{
  assert.equal(classifyVessel(row,{...options,createdAfter:new Set([id])}),'science');
  assert.equal(classifyVessel({...row,birthtimeMs:Date.parse(completedAt)+1},options),'science');
});
test('potentially mixed docking and transition-window provenance stays uncertain',()=>{
  assert.equal(classifyVessel(row,{...options,anyDocking:true}),'uncertain');
  assert.equal(classifyVessel({...row,birthtimeMs:Date.parse(preparedAt)},options),'uncertain');
  assert.equal(classifyVessel({...row,mtimeMs:Date.parse(preparedAt)-1},{...options,anyDocking:true}),'sandbox');
});
test('unsafe identities and arbitrary report payloads fail closed',()=>{
  assert.throws(()=>classifyVessel({...row,id:'../evil'},options));
  assert.throws(()=>projectHousekeeping({schemaVersion:1,status:'available',observedAt:completedAt}));
  assert.deepEqual(projectHousekeeping({schemaVersion:1,status:'unavailable',observedAt:completedAt,password:'secret'}),{schemaVersion:1,status:'unavailable',observedAt:completedAt});
});
